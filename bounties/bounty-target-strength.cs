using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using EpicLoot.Adventure;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot spawns a bounty's creatures at fixed levels, 1 or 2 stars for a target and none or 1 for an add, while the
    // creatures around them roll their stars from the biome, the bosses defeated and the distance from the centre. Roll a
    // bounty creature's level as StarLevelSystem would for an ordinary creature in that spot, and put EpicLoot's stars on top.
    //
    // EpicLoot also gives them more health as they spawn, through StarLevelSystem: three times as much for a gold bounty's
    // target, twice as much for an iron bounty's and one and a half times for adds. SLS adds the health of a creature's
    // stars beside that base, and kept the bonus only in the stats the spawning game worked out, so a creature lost it once
    // it was loaded again or passed to another player's game. EpicLoot also marks the target a boss, for a boss health bar,
    // and SLS then scaled it as one: +2% damage per star instead of +20%, boss modifiers instead of ordinary ones and, once
    // reloaded, +30% health per star instead of +75%. Give the bonus back from the bounty the creature carries whenever SLS
    // works out its stats, multiplying the health of its stars too, and let SLS scale, modify and evolve the target as the
    // ordinary creature it spawned as. The boss health bar stays.
    internal static class BountyTargetStrength
    {
        private static readonly Func<BountyInfo, bool, float> HealthModifier =
            AccessTools.MethodDelegate<Func<BountyInfo, bool, float>>(AccessTools.Method(typeof(BountyTarget), "GetMaxHealthModifier"));
        private static MethodInfo creatureSettings, ordinaryLevel;
        private static PropertyInfo perLevelValues;
        private static object healthPerLevel;
        // The creature EpicLoot is setting up as a bounty's target or add.
        private static Character spawning;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var cache = StarLevelSystem.CompositeLazyCache;
            var stats = StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.CreatureSetup.CreatureStatOverrides", true);
            var modifiers = StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.Modifiers.CreatureModifiers");
            var selection = StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.LevelSystem.LevelSelection");
            // SLS builds a creature's stats in GetAndSetLocalCache, whose last step reapplies the stats it saved.
            var restore = AccessTools.Method(cache, "ApplyPersistedStatOverrides", new[] { typeof(ZDO), StarLevelSystem.CharacterCacheEntry });
            // Every SLS step that scales, modifies or evolves a creature differently for a boss, each taking it first.
            var scaling = new[]
            {
                AccessTools.Method(cache, "GetAndSetLocalCache"),
                StarLevelSystem.DetermineDamageFactor,
                AccessTools.Method(modifiers, "SelectModifiersForCreature"),
                AccessTools.Method(modifiers, "TryRollEvolutionModifier"),
                AccessTools.Method(selection, "GetMaxCreatureLevel"),
                AccessTools.Method(stats, "Apply"),
            };
            var setStats = AccessTools.GetDeclaredMethods(stats).Single(method => method.Name == "SetInEntry" && method.GetParameters().Length == 4 && method.GetParameters()[0].ParameterType == typeof(Character));
            if (restore == null || scaling.Any(method => method == null || method.GetParameters().FirstOrDefault()?.ParameterType != typeof(Character)))
                throw new InvalidOperationException("StarLevelSystem's creature stats changed.");
            perLevelValues = AccessTools.Property(StarLevelSystem.CharacterCacheEntry, "CreaturePerLevelValueModifiers");
            var attribute = StarLevelSystem.Assembly.GetType("StarLevelSystem.common.DataObjects+CreaturePerLevelAttribute");
            healthPerLevel = attribute != null && Enum.IsDefined(attribute, "HealthPerLevel") ? Enum.Parse(attribute, "HealthPerLevel") : null;
            // A creature's own and its biome's settings, then the level SLS rolls from them for a creature that has none yet.
            creatureSettings = AccessTools.Method(selection, "SelectCreatureBiomeSettings");
            ordinaryLevel = creatureSettings == null ? null : AccessTools.Method(selection, "DetermineLevel", new[] { typeof(Character), typeof(ZDO) }
                .Concat(creatureSettings.GetParameters().Skip(2).Select(parameter => parameter.ParameterType.GetElementType())).Concat(new[] { typeof(int), typeof(bool) }).ToArray());
            var targetLevel = AccessTools.Method(typeof(BountyTarget), "GetTargetLevel");
            var setup = AccessTools.Method(typeof(BountyTarget), nameof(BountyTarget.Initialize));
            if (!typeof(IDictionary).IsAssignableFrom(perLevelValues?.PropertyType) || healthPerLevel == null ||
                creatureSettings?.GetParameters().Length != 5 || creatureSettings.GetParameters()[0].ParameterType != typeof(UnityEngine.GameObject) ||
                ordinaryLevel?.ReturnType != typeof(int) || targetLevel?.ReturnType != typeof(int) || setup == null)
                throw new InvalidOperationException("StarLevelSystem's level roll or EpicLoot's bounty setup changed.");
            var self = typeof(BountyTargetStrength);
            harmony.Patch(restore, postfix: new HarmonyMethod(self, nameof(RestoreBonus)));
            // The new stat/reset path writes configured values after the persisted-stat hook. Restore the bounty
            // bonus at that final write, before SLS applies health to the live creature.
            harmony.Patch(setStats, postfix: new HarmonyMethod(self, nameof(RestoreStatBonus)));
            foreach (var method in scaling)
                harmony.Patch(method, prefix: new HarmonyMethod(self, nameof(AsOrdinary)), finalizer: new HarmonyMethod(self, nameof(AsBoss)));
            harmony.Patch(setup, prefix: new HarmonyMethod(self, nameof(BeginSetup)), finalizer: new HarmonyMethod(self, nameof(EndSetup)));
            harmony.Patch(targetLevel, postfix: new HarmonyMethod(self, nameof(AddBiomeLevels)));
        }

        private static void BeginSetup(BountyTarget __instance) => spawning = __instance.GetComponent<Character>();

        private static void EndSetup() => spawning = null;

        // EpicLoot sets the level it reads here before anything else has given the new creature one, so SLS still rolls.
        private static void AddBiomeLevels(ref int __result)
        {
            var view = spawning ? GameFields.CharacterView(spawning) : null;
            var zdo = view != null ? view.GetZDO() : null;
            if (zdo == null) return;
            var settings = new object[] { spawning.gameObject, null, null, null, null };
            creatureSettings.Invoke(null, settings);
            int rolled = (int)ordinaryLevel.Invoke(null, new object[] { spawning, zdo, settings[2], settings[3], settings[4], 0, true });
            __result = Math.Max(rolled, 1) + Math.Max(__result, 1) - 1;
        }

        private static void RestoreStatBonus(Character __0, object __1) => RestoreBonus(GameFields.CharacterView(__0)?.GetZDO(), __1);

        // Like EpicLoot's, the bonus replaces the creature's base health multiplier. SLS adds the health of the creature's
        // stars beside the base instead of multiplying it, so the base also carries the bonus's share of that health.
        private static void RestoreBonus(ZDO __0, object __1)
        {
            if (__0 == null || __1 == null || !BountyCreature.TryRead(__0, out var bounty)) return;
            float bonus = HealthModifier(bounty, __0.GetBool(BountyCreature.IsAddKey));
            float starHealth = Convert.ToSingle(((IDictionary)perLevelValues.GetValue(__1))[healthPerLevel]) * (Math.Max(__0.GetInt(ZDOVars.s_level, 1), 1) - 1);
            StarLevelSystem.BaseValuesOf(__1)[StarLevelSystem.BaseHealth] = bonus + (bonus - 1f) * starHealth;
        }

        // EpicLoot marks only a bounty's target a boss; its adds stay ordinary.
        private static void AsOrdinary(Character __0, out bool __state)
        {
            var view = __0 != null && __0.m_boss ? GameFields.CharacterView(__0) : null;
            var zdo = view != null ? view.GetZDO() : null;
            __state = zdo != null && zdo.GetString(BountyCreature.IdKey).Length > 0;
            if (__state) __0.m_boss = false;
        }

        private static void AsBoss(Character __0, bool __state)
        {
            if (__state) __0.m_boss = true;
        }
    }
}
