using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // StarLevelSystem's Nemesis minibosses: a creature that kills a player can join a shared pool of named minibosses, and
    // a creature appearing near a player whose score is high enough can bring one from its biome. A new creature first
    // met the star changes, whose action cooldown then skipped every later check, then the ambushes, and only then the
    // miniboss, so a player rarely got its chance before the star changes' score costs pulled them back under its
    // threshold. The miniboss check now comes first, once per creature, with SLS's own checks, chance, spawning and pool
    // updates.
    internal static class NemesisMinibosses
    {
        private static readonly HashSet<ZDOID> Checked = new HashSet<ZDOID>();
        private static FieldInfo settings;
        private static PropertyInfo chanceChanges, creatureOps, action;
        private static MethodInfo spawner, isAspect, isSpawnManaged;
        private static string spawnMultiplied, marker;
        private static bool minibossOnly;
        // Spawn checks in progress: the creatures a spawn brings, such as a miniboss's escorts, are set up inside it.
        private static int depth;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            Type Named(string name) => StarLevelSystem.Assembly.GetTypes().Single(type => type.FullName == name);
            var actions = Named("StarLevelSystem.modules.NemesisSystem.NemesisActions");
            var data = Named("StarLevelSystem.common.DataObjects");
            settings = AccessTools.Field(Named("StarLevelSystem.Data.NemesisSystemData"), "SLE_Nemesis_Settings");
            chanceChanges = AccessTools.Property(settings?.FieldType, "ChanceChanges");
            creatureOps = AccessTools.Property(chanceChanges?.PropertyType, "CreatureOps");
            action = AccessTools.Property(data.GetNestedType("NemesisChanceEntry"), "Action");
            spawner = AccessTools.Method(actions, "NemesisRandomSpawner", new[] { typeof(Character) });
            isAspect = AccessTools.Method(StarLevelSystem.Assembly.GetTypes().Single(type => type.Name == "BossPhases"), "IsAspect", new[] { typeof(Character) });
            isSpawnManaged = AccessTools.Method(StarLevelSystem.CompositeLazyCache, "IsSpawnManaged", new[] { typeof(Character) });
            spawnMultiplied = AccessTools.Field(data, "SLS_SPAWN_MULT")?.GetValue(null) as string;
            marker = AccessTools.Field(data, "SLS_NEMESIS_BOSS")?.GetValue(null) as string;
            var influence = AccessTools.Method(actions, "LevelSystemDetermineNemesisInfluence");
            if (creatureOps == null || action == null || spawner == null || isAspect == null || isSpawnManaged == null || spawnMultiplied == null || marker == null ||
                influence?.GetParameters().FirstOrDefault()?.ParameterType != typeof(Character))
                throw new InvalidOperationException("StarLevelSystem's Nemesis minibosses changed.");
            harmony.Patch(influence, prefix: new HarmonyMethod(typeof(NemesisMinibosses), nameof(MinibossFirst)));
            harmony.Patch(spawner, prefix: new HarmonyMethod(typeof(NemesisMinibosses), nameof(OrderSpawns)),
                finalizer: new HarmonyMethod(typeof(NemesisMinibosses), nameof(RestoreSpawns)));
        }

        // The named miniboss itself, which SLS marks on the creature; its escorts carry no mark.
        internal static bool Is(ZDO creature) => marker != null && creature.GetBool(marker);

        // A new creature's star changes: first its one miniboss check, through SLS's spawn checks. A miniboss starts the
        // action cooldown, which skips the star changes and the later spawn checks for this creature.
        private static void MinibossFirst(Character __0)
        {
            var character = __0;
            if (depth > 0 || !character || character.IsBoss() || character.IsTamed() || !Spawns(character)) return;
            Checked.Add(character.GetZDOID());
            bool previous = minibossOnly;
            minibossOnly = true;
            try { spawner.Invoke(null, new object[] { character }); }
            finally { minibossOnly = previous; }
        }

        // Only creatures SLS's spawn-rate step will reach run its spawn checks; it marks each once.
        private static bool Spawns(Character character)
        {
            var data = character.GetComponent<ZNetView>()?.GetZDO();
            return data != null && !data.GetBool(spawnMultiplied) &&
                !(bool)isAspect.Invoke(null, new object[] { character }) && !(bool)isSpawnManaged.Invoke(null, new object[] { character });
        }

        private static bool IsMiniboss(object entry) => action.GetValue(entry).ToString() == "SpawnMiniboss";

        // The spawn checks see the miniboss alone in the first check, then the rest without it. A creature SLS sets up
        // without a star roll has its miniboss check first among them. Creatures set up during a spawn, which SLS does
        // before it starts the action cooldown, bring nothing, so a party or miniboss never brings another; nor do a hunting
        // pack's creatures and their offspring later on.
        private static void OrderSpawns(Character __0, out IDictionary __state)
        {
            __state = null;
            bool nested = depth++ > 0 || NemesisHuntingPacks.Descends(__0);
            var changes = chanceChanges.GetValue(settings.GetValue(null));
            if (!(creatureOps.GetValue(changes) is IDictionary all)) return;
            bool minibosses = !nested && (minibossOnly || !(__0 && Checked.Remove(__0.GetZDOID())));
            bool others = !nested && !minibossOnly;
            var ordered = (IDictionary)Activator.CreateInstance(all.GetType());
            foreach (DictionaryEntry entry in all)
                if (minibosses && IsMiniboss(entry.Value)) ordered.Add(entry.Key, entry.Value);
            foreach (DictionaryEntry entry in all)
                if (others && !IsMiniboss(entry.Value)) ordered.Add(entry.Key, entry.Value);
            creatureOps.SetValue(changes, ordered);
            __state = all;
        }

        private static Exception RestoreSpawns(IDictionary __state, Exception __exception)
        {
            depth--;
            if (__state != null) creatureOps.SetValue(chanceChanges.GetValue(settings.GetValue(null)), __state);
            return __exception;
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        private static class EndSession
        {
            private static void Postfix() => Checked.Clear();
        }
    }
}
