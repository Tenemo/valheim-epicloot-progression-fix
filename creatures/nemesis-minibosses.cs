using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // StarLevelSystem's Nemesis minibosses: a creature that kills a player can join a shared pool of named minibosses, and
    // a creature appearing near a player whose score is high enough can bring one from its biome. A new creature first
    // met the star changes, whose action cooldown then skipped every later check, then the ambushes, and only then the
    // miniboss, so a player rarely got its chance before the star changes' score costs pulled them back under its
    // threshold. The miniboss check now comes first, once per creature, with SLS's own checks, chance, spawning and pool
    // updates.
    //
    // SLS keeps each miniboss for the biome where its creature killed a player, so a Fuling that killed someone at the
    // forest's edge came back as a Black Forest miniboss, and names it after the player's character model, "Killer of
    // Human". A miniboss now comes only to a biome where the world spawns its creature on its own; a creature the world
    // never spawns on its own, such as a crypt's skeletons, keeps the biome where it killed. It is named after the player it
    // killed, and its escorts come from the biome it appears in. Minibosses already in the pool follow the same rules.
    internal static class NemesisMinibosses
    {
        private static readonly HashSet<ZDOID> Checked = new HashSet<ZDOID>();
        private static readonly Regex KilledPlayer = new Regex(@"^(.*\$SLS_Miniboss_postfix\d+) .+$", RegexOptions.Singleline);
        private static FieldInfo settings;
        private static PropertyInfo chanceChanges, creatureOps, action, available, bossSpawn, minions, killedPlayerName, recordedBiome, prefab, customName;
        private static MethodInfo spawner, isAspect, isSpawnManaged, generateMinions, spawnOne;
        private static string spawnMultiplied, marker;
        private static bool minibossOnly;
        // Spawn checks in progress: the creatures a spawn brings, such as a miniboss's escorts, are set up inside it.
        private static int depth;
        // The biomes where the world spawns each creature on its own, by prefab name.
        private static Dictionary<string, Heightmap.Biome> homes;
        // The miniboss SLS picked and is spawning, and the biome it was picked for.
        private static object picked;
        private static Heightmap.Biome pickedFor;

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
            var miniboss = data.GetNestedType("NemesisMiniboss");
            var spawn = data.GetNestedType("NemesisSpawn");
            available = AccessTools.Property(settings?.FieldType, "AvailableMiniBosses");
            bossSpawn = AccessTools.Property(miniboss, "BossSpawn");
            minions = AccessTools.Property(miniboss, "Minions");
            killedPlayerName = AccessTools.Property(miniboss, "KilledPlayerName");
            recordedBiome = AccessTools.Property(miniboss, "Biome");
            prefab = AccessTools.Property(spawn, "Prefab");
            customName = AccessTools.Property(spawn, "CustomName");
            var manager = Named("StarLevelSystem.modules.NemesisSystem.NemesisMiniBossManager");
            var select = AccessTools.Method(manager, "RandomlySelectAppropriateMiniboss", new[] { typeof(Heightmap.Biome) });
            generateMinions = AccessTools.Method(manager, "GenerateMinionsForBiome", new[] { typeof(Heightmap.Biome), typeof(int), typeof(int) });
            var remote = Named("StarLevelSystem.modules.NemesisSystem.NemesisRemoteSpawnControl");
            spawnOne = AccessTools.Method(remote, "SpawnNemesisSpawn");
            var group = AccessTools.Method(remote, "SpawnMinibossGroup");
            var groupParameters = group?.GetParameters();
            if (groupParameters?.Length != 5 || groupParameters[0].ParameterType != miniboss || groupParameters[2].ParameterType != typeof(Heightmap.Biome) ||
                creatureOps == null || action == null || spawner == null || isAspect == null || isSpawnManaged == null || spawnMultiplied == null || marker == null ||
                influence?.GetParameters().FirstOrDefault()?.ParameterType != typeof(Character) || available == null || bossSpawn?.PropertyType != spawn ||
                minions == null || killedPlayerName?.PropertyType != typeof(string) || recordedBiome?.PropertyType != typeof(Heightmap.Biome) ||
                prefab?.PropertyType != typeof(string) || customName?.PropertyType != typeof(string) || select?.ReturnType != miniboss ||
                generateMinions == null || spawnOne?.GetParameters().FirstOrDefault()?.ParameterType != spawn || spawnOne.GetParameters().Length != 7)
                throw new InvalidOperationException("StarLevelSystem's Nemesis minibosses changed.");
            harmony.Patch(influence, prefix: new HarmonyMethod(typeof(NemesisMinibosses), nameof(MinibossFirst)));
            harmony.Patch(spawner, prefix: new HarmonyMethod(typeof(NemesisMinibosses), nameof(OrderSpawns)),
                finalizer: new HarmonyMethod(typeof(NemesisMinibosses), nameof(RestoreSpawns)));
            harmony.Patch(select, prefix: new HarmonyMethod(typeof(NemesisMinibosses), nameof(PickNative)));
            harmony.Patch(spawnOne, prefix: new HarmonyMethod(typeof(NemesisMinibosses), nameof(SpawnPicked)),
                finalizer: new HarmonyMethod(typeof(NemesisMinibosses), nameof(SpawnedPicked)));
            harmony.Patch(group, prefix: new HarmonyMethod(typeof(NemesisMinibosses), nameof(PickRemote)),
                finalizer: new HarmonyMethod(typeof(NemesisMinibosses), nameof(PlacedRemote)));
        }

        // The biomes where the world spawns a creature on its own: its enabled entries in the world's spawn lists that need
        // neither a defeated boss nor a world event. Those that do are invasions of other biomes, such as the Fulings that
        // roam the Meadows, Black Forest and Mountains at night once Yagluth is dead.
        internal static Heightmap.Biome Home(string creature)
        {
            if (homes == null)
            {
                var found = new Dictionary<string, Heightmap.Biome>();
                var system = ZNetScene.instance?.GetPrefab("_ZoneCtrl")?.GetComponent<SpawnSystem>();
                if (!system) return Heightmap.Biome.None;
                foreach (var list in system.m_spawnLists)
                    foreach (var entry in list.m_spawners)
                        if (entry.m_enabled && entry.m_prefab && string.IsNullOrEmpty(entry.m_requiredGlobalKey) && string.IsNullOrEmpty(entry.m_requiredPersistentEvent))
                        {
                            found.TryGetValue(entry.m_prefab.name, out var biomes);
                            found[entry.m_prefab.name] = biomes | entry.m_biome;
                        }
                homes = found;
            }
            return homes.TryGetValue(creature ?? "", out var home) ? home : Heightmap.Biome.None;
        }

        // A pooled miniboss's biomes: where the world spawns its creature, or, for a creature it never spawns on its own,
        // where it killed.
        private static Heightmap.Biome MinibossHome(object miniboss)
        {
            var home = Home((string)prefab.GetValue(bossSpawn.GetValue(miniboss)));
            return home != Heightmap.Biome.None ? home : (Heightmap.Biome)recordedBiome.GetValue(miniboss);
        }

        // SLS's pick of a random pooled miniboss for a biome, by where its creature lives instead of where it killed. The
        // picked miniboss stays the pool's own, so SLS still removes it from every player's pool.
        private static bool PickNative(Heightmap.Biome __0, ref object __result)
        {
            var pool = ((IList)available.GetValue(settings.GetValue(null))).Cast<object>().Where(entry => bossSpawn.GetValue(entry) != null).ToList();
            for (int index = pool.Count - 1; index > 0; index--)
            {
                int other = UnityEngine.Random.Range(0, index + 1);
                (pool[index], pool[other]) = (pool[other], pool[index]);
            }
            __result = pool.FirstOrDefault(entry => (MinibossHome(entry) & __0) != Heightmap.Biome.None);
            picked = __result;
            pickedFor = __0;
            return false;
        }

        // The picked miniboss's spawns: SLS spawns its escorts, then the miniboss. Its stored escorts come from where it
        // killed, so they are left out and the biome's own escorts follow the miniboss instead; the miniboss carries the
        // name of the player it killed for this spawn, and the stored name stays as SLS saved it.
        private static bool SpawnPicked(object __0, out string __state)
        {
            __state = null;
            if (picked == null || __0 == null) return true;
            if (minions.GetValue(picked) is IList stored && stored.Contains(__0)) return false;
            if (__0 != bossSpawn.GetValue(picked)) return true;
            __state = (string)customName.GetValue(__0);
            string player = (string)killedPlayerName.GetValue(picked);
            if (__state != null && !string.IsNullOrEmpty(player))
                customName.SetValue(__0, KilledPlayer.Replace(__state, match => match.Groups[1].Value + " " + player));
            return true;
        }

        private static Exception SpawnedPicked(object __0, Vector3 __1, Quaternion __2, int __3, string __state, Exception __exception)
        {
            if (picked == null || __0 == null || __0 != bossSpawn.GetValue(picked)) return __exception;
            customName.SetValue(__0, __state);
            var biome = pickedFor;
            picked = null;
            if (__exception == null && generateMinions.Invoke(null, new object[] { biome, 1, 4 }) is IList escorts)
                foreach (var escort in escorts)
                    spawnOne.Invoke(null, new object[] { escort, __1, __2, __3, null, null, null });
            return __exception;
        }

        // SLS's remote spawner places a pooled miniboss later, from a saved copy, for the biome it was picked for. The copy
        // follows the same rules as the pool's own miniboss.
        private static void PickRemote(object __0, Heightmap.Biome __2)
        {
            picked = __0;
            pickedFor = __2;
        }

        private static Exception PlacedRemote(Exception __exception)
        {
            picked = null;
            return __exception;
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
            if (--depth == 0) picked = null;
            if (__state != null) creatureOps.SetValue(chanceChanges.GetValue(settings.GetValue(null)), __state);
            return __exception;
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        private static class EndSession
        {
            // Another server may bring other spawn lists.
            private static void Postfix() { Checked.Clear(); homes = null; }
        }
    }
}
