using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class StarLevelModifierIcons
    {
        internal static void Initialize(ManualLogSource logger)
        {
            if (!StarLevelSystem.Installed) return;
            var modifiers = StarLevelSystem.Assembly.GetType("StarLevelSystem.Data.CreatureModifiersData");
            var definitions = (IDictionary)AccessTools.Field(modifiers, "ModifierDefinitions").GetValue(null);
            var definition = definitions["EitrDrain"];
            var icon = AccessTools.Property(definition.GetType(), "StarVisual");
            var sprites = AccessTools.Field(modifiers, "LoadedModifierSprites")?.GetValue(null) as IDictionary;
            var style = AccessTools.Field(modifiers, "SelectedModifierDisplayStyle");
            var bundle = AccessTools.Field(StarLevelSystem.Assembly.GetType("StarLevelSystem.StarLevelSystem"), "EmbeddedResourceBundle")?.GetValue(null) as AssetBundle;
            if (icon?.PropertyType != typeof(string) || !icon.CanWrite || sprites == null || style == null || !bundle)
                throw new InvalidOperationException("StarLevelSystem's modifier icon loading changed.");

            // Both icon styles contain eitreater.png; the upstream definition asks for eitrdrain.png.
            bool existed = sprites.Contains("eitreater");
            var sprite = existed ? sprites["eitreater"] as Sprite : bundle.LoadAsset<Sprite>(
                "assets/custom/starlevels/" + (Convert.ToInt32(style.GetValue(null)) == 1 ? "icons2" : "icons") + "/eitreater.png");
            if (!sprite) throw new InvalidOperationException("StarLevelSystem's Eitr modifier icon is unavailable.");
            string previous = (string)icon.GetValue(definition);
            IntegrationChanges.Defer(() => { icon.SetValue(definition, "eitreater"); if (!existed) sprites.Add("eitreater", sprite); },
                () => { icon.SetValue(definition, previous); if (!existed) sprites.Remove("eitreater"); });
            logger.LogInfo("Eitr-draining creatures use StarLevelSystem's bundled Eitr icon.");
        }
    }

    // The StarLevelSystem members that several patches use, looked up once, on first use. Without StarLevelSystem the
    // patches that use them stay off; if it no longer has one of them, the gameplay patches fail at startup.
    internal static class StarLevelSystem
    {
        internal static Assembly Assembly;
        internal static Type CompositeLazyCache;
        // What SLS keeps about a creature, such as its untranslated name and its base value modifiers.
        internal static Type CharacterCacheEntry;
        // A creature's damage factor, from its stars and saved damage multiplier together.
        internal static MethodInfo DetermineDamageFactor;
        // Keys of a creature's base value modifiers.
        internal static object BaseHealth, BaseDamage;
        // LevelSelection.DetermineLevelRollResult, a creature's level roll, whose nightBonus parameter multiplies its
        // level-up chances.
        internal static MethodInfo LevelRoll;
        private static MethodInfo getCacheEntry;
        private static PropertyInfo baseValueModifiers, creatureName;

        internal static void Initialize()
        {
            if (!PluginIds.TryGet(PluginIds.StarLevelSystem, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            CompositeLazyCache = assembly.GetType("StarLevelSystem.Data.CompositeLazyCache");
            getCacheEntry = AccessTools.Method(CompositeLazyCache, "GetCacheEntry", new[] { typeof(Character) });
            CharacterCacheEntry = getCacheEntry?.ReturnType;
            baseValueModifiers = AccessTools.Property(CharacterCacheEntry, "CreatureBaseValueModifiers");
            creatureName = AccessTools.Property(CharacterCacheEntry, "CreatureNameLocalizable");
            var attribute = assembly.GetType("StarLevelSystem.common.DataObjects+CreatureBaseAttribute");
            BaseHealth = attribute != null && Enum.IsDefined(attribute, "BaseHealth") ? Enum.Parse(attribute, "BaseHealth") : null;
            BaseDamage = attribute != null && Enum.IsDefined(attribute, "BaseDamage") ? Enum.Parse(attribute, "BaseDamage") : null;
            DetermineDamageFactor = AccessTools.Method(assembly.GetType("StarLevelSystem.modules.Damage.DamagePatches+ModifyDamagePerLevel"),
                "DetermineDamageFactor", new[] { typeof(Character) });
            if (baseValueModifiers == null || creatureName?.PropertyType != typeof(string) || BaseHealth == null || BaseDamage == null ||
                DetermineDamageFactor?.ReturnType != typeof(float))
                throw new InvalidOperationException("StarLevelSystem's creature cache or damage factor changed.");
            var chances = typeof(SortedDictionary<int, float>);
            LevelRoll = AccessTools.Method(assembly.GetType("StarLevelSystem.modules.LevelSystem.LevelSelection"), "DetermineLevelRollResult",
                new[] { typeof(float), typeof(int), chances, chances, typeof(float), typeof(float), typeof(float), typeof(float), typeof(float) });
            if (LevelRoll?.ReturnType != typeof(int) || LevelRoll.GetParameters()[5].Name != "nightBonus")
                throw new InvalidOperationException("StarLevelSystem's level roll changed.");
            Assembly = assembly;
        }

        internal static bool Installed => Assembly != null;

        // Null for a creature SLS keeps nothing about.
        internal static object CacheEntryOf(Character character) => getCacheEntry.Invoke(null, new object[] { character });

        internal static IDictionary BaseValuesOf(object cacheEntry) => baseValueModifiers.GetValue(cacheEntry) as IDictionary;

        // The untranslated species name with the creature's modifiers, such as "$mod_alert $enemy_lox"; null when SLS keeps
        // nothing about the creature.
        internal static string CreatureNameOf(Character character)
        {
            var entry = CacheEntryOf(character);
            return entry == null ? null : (string)creatureName.GetValue(entry);
        }
    }

    // The pack already tunes stars, creature sizes and ownerless novas against its native world settings. SLS 1.25
    // added these global multipliers to those paths. Keep the established tuning without changing ordinary combat.
    internal static class StarLevelWorldBalance
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var rates = StarLevelSystem.Assembly.GetType("StarLevelSystem.common.WorldRates", true);
            var chance = AccessTools.PropertyGetter(rates, "LevelUpRate");
            var size = AccessTools.Method(rates, "CreatureSizeFactor");
            var nova = AccessTools.Method(StarLevelSystem.Assembly.GetType("StarLevelSystem.Modifiers.DeathNova+DeathNovaOnDeathPatch", true), "TriggerNova");
            if (chance?.ReturnType != typeof(float) || size?.ReturnType != typeof(float) || nova == null)
                throw new InvalidOperationException("StarLevelSystem's world multipliers changed.");
            var neutral = new HarmonyMethod(typeof(StarLevelWorldBalance), nameof(KeepPackFactor));
            harmony.Patch(chance, postfix: neutral);
            harmony.Patch(size, postfix: neutral);
            harmony.Patch(nova, transpiler: new HarmonyMethod(typeof(StarLevelWorldBalance), nameof(KeepNovaDamage)));
        }

        private static void KeepPackFactor(ref float __result) => __result = 1f;

        private static IEnumerable<CodeInstruction> KeepNovaDamage(IEnumerable<CodeInstruction> instructions)
        {
            var multiplier = AccessTools.Field(typeof(Game), "m_enemyDamageRate");
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.LoadsField(multiplier))
                {
                    instruction.opcode = OpCodes.Ldc_R4;
                    instruction.operand = 1f;
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1) throw new InvalidOperationException("StarLevelSystem's nova world multiplier changed.");
        }
    }
}
