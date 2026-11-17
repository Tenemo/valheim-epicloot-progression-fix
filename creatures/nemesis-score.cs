using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class NemesisScore
    {
        private const string SnapshotKey = Plugin.Id + ".nemesis-score";
        private sealed class Life { internal bool Initialized, Died; }
        private static readonly ConditionalWeakTable<Player, Life> Lives = new ConditionalWeakTable<Player, Life>();
        private static Func<Player, float> readScore;
        private static Action<Player, float> setScore;
        private static Action<Player> deathPenalty;
        private static ConfigEntry<bool> enabled;
        private static FieldInfo settings;
        private static PropertyInfo scoreSettings, maximum;
        private static ManualLogSource logger;

        internal static void Initialize(IntegrationPatches patches, ManualLogSource log)
        {
            if (!StarLevelSystem.Installed) return;
            var assembly = StarLevelSystem.Assembly;
            var score = assembly.GetType("StarLevelSystem.modules.NemesisSystem.NemesisScoreSystem", true);
            var get = AccessTools.Method(score, "GetScore", new[] { typeof(Player) });
            var set = AccessTools.Method(score, "SetScore", new[] { typeof(Player), typeof(float) });
            var change = AccessTools.Method(score, "UpdateScore", new[] { typeof(Player), typeof(float) });
            var load = AccessTools.Method(score, "LoadScoreData", new[] { typeof(Player) });
            var died = AccessTools.Method(score, "PlayerDeathScoreChange", new[] { typeof(Player) });
            var attach = AccessTools.Method(assembly.GetType("StarLevelSystem.modules.NemesisSystem.NemesisPatches+AttachNemesisManagerOnSpawn", true), "Postfix", new[] { typeof(Player) });
            var option = AccessTools.Field(assembly.GetType("StarLevelSystem.common.ValConfig", true), "EnableNemesisSystem");
            settings = AccessTools.Field(assembly.GetType("StarLevelSystem.Data.NemesisSystemData", true), "SLE_Nemesis_Settings");
            scoreSettings = AccessTools.Property(settings?.FieldType, "ScoreSystem");
            maximum = AccessTools.Property(scoreSettings?.PropertyType, "MaxScore");
            if (get?.ReturnType != typeof(float) || set?.ReturnType != typeof(void) || change?.ReturnType != typeof(float) ||
                load?.ReturnType != typeof(void) || died?.ReturnType != typeof(void) || attach?.ReturnType != typeof(void) || option?.FieldType != typeof(ConfigEntry<bool>) ||
                maximum?.PropertyType != typeof(float)) throw new InvalidOperationException("StarLevelSystem's Nemesis score lifecycle changed.");
            readScore = AccessTools.MethodDelegate<Func<Player, float>>(get);
            setScore = AccessTools.MethodDelegate<Action<Player, float>>(set);
            deathPenalty = AccessTools.MethodDelegate<Action<Player>>(died);
            enabled = (ConfigEntry<bool>)option.GetValue(null); logger = log;
            var type = typeof(NemesisScore);
            patches.Patch(AccessTools.Method(typeof(Player), nameof(Player.OnDeath)),
                prefix: new HarmonyMethod(type, nameof(Died)) { priority = Priority.First });
            patches.Patch(set, postfix: new HarmonyMethod(type, nameof(Changed)));
            patches.Patch(change, postfix: new HarmonyMethod(type, nameof(Changed)));
            patches.Patch(load, postfix: new HarmonyMethod(type, nameof(Loaded)));
            patches.Patch(attach, prefix: new HarmonyMethod(type, nameof(LoadActualPlayer)));
            patches.Patch(AccessTools.Method(typeof(Player), nameof(Player.Save)), prefix: new HarmonyMethod(type, nameof(Saving)));
        }

        private static bool ActualPlayer(Player player)
        {
            if (!player || player != Player.m_localPlayer) return false;
            var view = GameFields.CharacterView(player);
            return view && view.IsValid() && view.IsOwner();
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool LoadActualPlayer(Player __0) => ActualPlayer(__0);

        private static void Capture(Player player)
        {
            float value = readScore(player);
            if (Finite(value)) player.m_customData[SnapshotKey] = value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void Changed(Player player)
        {
            if (!enabled.Value || !ActualPlayer(player)) return;
            Lives.GetValue(player, _ => new Life()).Initialized = true;
            Capture(player);
        }

        private static void Saving(Player __instance)
        {
            if (enabled.Value && ActualPlayer(__instance) && Lives.TryGetValue(__instance, out var life) && life.Initialized) Capture(__instance);
        }

        private static void Loaded(Player player)
        {
            if (!enabled.Value || !ActualPlayer(player)) return;
            var life = Lives.GetValue(player, _ => new Life());
            life.Died = false;
            float value = readScore(player);
            if (player.m_customData.TryGetValue(SnapshotKey, out string saved))
            {
                if (float.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out float snapshot) && Finite(snapshot)) value = snapshot;
                else logger.LogWarning("Cannot read this character's saved Nemesis score; using its current score or the starting score.");
            }
            if (!Finite(value)) value = (float)maximum.GetValue(scoreSettings.GetValue(settings.GetValue(null))) * .5f;
            // The native setter clamps current bounds and updates both the network value and the local cache.
            setScore(player, value);
            life.Initialized = true;
        }

        private static void Died(Player __instance)
        {
            if (!enabled.Value || !ActualPlayer(__instance)) return;
            var life = Lives.GetValue(__instance, _ => new Life());
            if (life.Died) return;
            life.Died = true;
            deathPenalty(__instance);
        }
    }
}
