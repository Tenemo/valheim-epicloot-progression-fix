using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // FedoHud 1.1.2 reads the death count with PlayerProfile.GetStat(PlayerStatType), which Valheim 1.0.21 replaced with
    // GetStat(PlayerStatType, DifficultyRequirement). The missing method threw before the weight and armor numbers were
    // set, so all three stayed blank. Its death count now reads the lifetime total across difficulties.
    internal static class FedoHudDeaths
    {
        private static FieldInfo deathText;
        private static MethodInfo deaths;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.FedoHud, out var plugin)) return;
            var overlay = plugin.Instance.GetType().Assembly.GetType("FedoHud.PlayerStatsOverlay");
            var refresh = AccessTools.Method(overlay, "Refresh", new[] { typeof(bool), typeof(bool), typeof(bool) });
            deathText = AccessTools.Field(overlay, "_deathText");
            if (refresh == null || deathText?.FieldType != typeof(TMP_Text)) throw new InvalidOperationException("FedoHud's player stats overlay changed.");
            // Valheim 1.0.20, which the dedicated server still runs, keeps the method FedoHud calls.
            deaths = AccessTools.Method(typeof(PlayerProfile), nameof(PlayerProfile.GetStat), new[] { typeof(PlayerStatType), typeof(DifficultyRequirement) });
            if (deaths != null) harmony.Patch(refresh, transpiler: new HarmonyMethod(typeof(FedoHudDeaths), nameof(CurrentDeaths)));
        }

        private static IEnumerable<CodeInstruction> CurrentDeaths(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.operand is MethodInfo method && method.Name == "RefreshDeath",
                AccessTools.Method(typeof(FedoHudDeaths), nameof(RefreshDeaths)), 1, "FedoHud's death counter refresh changed.");

        private static void RefreshDeaths()
        {
            var profile = Game.instance ? Game.instance.GetPlayerProfile() : null;
            ((TMP_Text)deathText.GetValue(null)).text = profile == null ? "" :
                ((int)(float)deaths.Invoke(profile, new object[] { PlayerStatType.Deaths, DifficultyRequirement.RawStats })).ToString();
        }
    }

    internal static class FedoHudPosition
    {
        private static MethodInfo defaultPosition;
        private static string preferenceKey;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.FedoHud, out var plugin)) return;
            var type = plugin.Instance.GetType();
            var saved = AccessTools.PropertyGetter(type, "SavedPlayerStatsPosition");
            defaultPosition = AccessTools.PropertyGetter(type, "DefaultPlayerStatsPosition");
            var save = AccessTools.Method(type, "SavePlayerStatsPosition", new[] { typeof(Vector2) });
            var layout = AccessTools.Method(type.Assembly.GetType("FedoHud.HudLayout"),
                "ResolveCenteredBelowMinimapPosition", new[] { typeof(Vector2), typeof(Vector2), typeof(RectTransform) });
            if (saved == null || defaultPosition == null || save == null || layout == null)
                throw new InvalidOperationException("FedoHud player-stat position API changed.");

            // Unity preferences live outside BepInEx/config, which Gale replaces during a profile pull.
            // Scope the key by profile so the isolated test client's layout cannot replace the production one.
            using (var hash = SHA256.Create())
                preferenceKey = "EpicLootProgressionFix.FedoHud.PlayerStats." + BitConverter.ToString(
                    hash.ComputeHash(Encoding.UTF8.GetBytes(plugin.Instance.Config.ConfigFilePath.ToLowerInvariant()))).Replace("-", "");
            harmony.Patch(saved, postfix: new HarmonyMethod(typeof(FedoHudPosition), nameof(RestorePosition)));
            harmony.Patch(save, postfix: new HarmonyMethod(typeof(FedoHudPosition), nameof(SavePosition)));
            harmony.Patch(layout, postfix: new HarmonyMethod(typeof(FedoHudPosition), nameof(LowerDefault)));
        }

        private static void RestorePosition(object __instance, ref Vector2 __result)
        {
            if (PlayerPrefs.HasKey(preferenceKey + ".x") && PlayerPrefs.HasKey(preferenceKey + ".y"))
            {
                var saved = new Vector2(PlayerPrefs.GetFloat(preferenceKey + ".x"), PlayerPrefs.GetFloat(preferenceKey + ".y"));
                if (IsFinite(saved)) { __result = saved; return; }
            }
            // Preserve an existing custom config position on first load, if the import has retained it.
            if (__result != (Vector2)defaultPosition.Invoke(__instance, null)) SavePosition(__result);
        }

        private static bool IsFinite(Vector2 position) => !float.IsNaN(position.x) && !float.IsInfinity(position.x) &&
            !float.IsNaN(position.y) && !float.IsInfinity(position.y);

        private static void SavePosition(Vector2 __0)
        {
            if (!IsFinite(__0)) return;
            PlayerPrefs.SetFloat(preferenceKey + ".x", __0.x);
            PlayerPrefs.SetFloat(preferenceKey + ".y", __0.y);
            PlayerPrefs.Save();
        }

        private static void LowerDefault(Vector2 saved, Vector2 defaultValue, ref Vector2 __result)
        {
            // In FedoHud 1.1.2 only the player-stat block and its Reset action use this layout method.
            // Preserve its minimap-centered X; a config Y override would bypass that automatic alignment.
            if (saved == defaultValue) __result.y -= 500f;
        }
    }

    internal static class FedoHudSkills
    {
        internal static void Initialize(ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.FedoHud, out var plugin)) return;
            var field = AccessTools.Field(plugin.Instance.GetType(), "_skillsList");
            if (field?.FieldType != typeof(ConfigEntry<string>))
                throw new InvalidOperationException("FedoHud skill selection API changed.");

            var shared = (ConfigEntry<string>)field.GetValue(plugin.Instance);
            // The picker and overlay both read this entry. Redirect it before play so every selection, including
            // clearing the last skill, is saved in this profile outside the config directory Gale replaces.
            var personal = new ConfigFile(Path.Combine(Paths.BepInExRootPath, "fedo-hud-state", "skills.dat"), false);
            personal.SaveOnConfigSet = false;
            var selection = personal.Bind("Skills", "SkillsList", shared.Value,
                "Skills selected in FedoHud.");
            IntegrationChanges.Defer(() => { field.SetValue(plugin.Instance, selection); personal.SaveOnConfigSet = true; },
                () => { personal.SaveOnConfigSet = false; field.SetValue(plugin.Instance, shared); });
            IntegrationChanges.Save(personal);
            // Adopt any selection still present on first load, then keep personal choices out of the owner's Push.
            IntegrationChanges.ConfigValue(shared, "");
            log.LogInfo("FedoHud skill selection is stored in BepInEx/fedo-hud-state/skills.dat, outside Gale-managed config.");
        }
    }

    internal static class FedoHudPreferences
    {
        internal static void Initialize(ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.FedoHud, out var plugin)) return;
            var personal = new ConfigFile(Path.Combine(Paths.BepInExRootPath, "fedo-hud-state", "preferences.dat"), false);
            personal.SaveOnConfigSet = false;
            foreach (var field in AccessTools.GetDeclaredFields(plugin.Instance.GetType()))
            {
                // These entries are read by both the live overlays and the menu controls. Shared values seed
                // missing preferences once; later imports can change pack defaults without resetting a choice.
                if (field.FieldType == typeof(ConfigEntry<bool>))
                {
                    var shared = (ConfigEntry<bool>)field.GetValue(plugin.Instance);
                    var selected = personal.Bind(shared.Definition, shared.Value, shared.Description);
                    IntegrationChanges.Defer(() => field.SetValue(plugin.Instance, selected), () => field.SetValue(plugin.Instance, shared));
                }
                else if (field.FieldType == typeof(ConfigEntry<float>) && field.Name.Contains("Position") &&
                    !field.Name.StartsWith("_playerStats", StringComparison.Ordinal))
                {
                    // The player-stat block already has durable per-profile Unity preferences.
                    var shared = (ConfigEntry<float>)field.GetValue(plugin.Instance);
                    var selected = personal.Bind(shared.Definition, shared.Value, shared.Description);
                    IntegrationChanges.Defer(() => field.SetValue(plugin.Instance, selected), () => field.SetValue(plugin.Instance, shared));
                }
            }
            IntegrationChanges.Defer(() => personal.SaveOnConfigSet = true, () => personal.SaveOnConfigSet = false);
            IntegrationChanges.Save(personal);
            log.LogInfo("FedoHud toggles and overlay positions are stored outside shared config in BepInEx/fedo-hud-state/preferences.dat.");
        }
    }
}
