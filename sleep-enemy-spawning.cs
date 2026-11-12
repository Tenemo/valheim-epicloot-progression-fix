using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static partial class SleepEnemyPause
    {
        // Each clock key of a prefab, with the age in seconds from which every reader of that clock acts the same however much
        // older it is: the spawn interval times the most spawns rolled at once, the respawn, trigger, hatch and growth times,
        // a full regeneration and being fed. Such a clock already that old when the night started stays as it is, since a
        // shifted copy would be read the same way; leaving the far, long-unvisited areas' objects unwritten keeps the next
        // save from rewriting almost the whole world. A clock without a known age always shifts.
        private sealed class NativeClockKeys
        {
            internal readonly Dictionary<int, double> Ticks = new(), Seconds = new();
            internal bool Character, Neutral;
        }
        private const double SettledMarginSeconds = 1d;
        // The game keeps a ZDO's long and float values in these per-object tables; reading them visits only the objects and
        // clocks that exist instead of every world object and each of the hundreds of keys a zone's spawner could hold.
        private static readonly FieldInfo StoredLongsField = AccessTools.Field(typeof(ZDOExtraData), "s_longs");
        private static readonly FieldInfo StoredFloatsField = AccessTools.Field(typeof(ZDOExtraData), "s_floats");
        private static readonly AccessTools.FieldRef<BinarySearchDictionary<int, long>, int[]> LongKeys = AccessTools.FieldRefAccess<BinarySearchDictionary<int, long>, int[]>("m_keys");
        private static readonly AccessTools.FieldRef<BinarySearchDictionary<int, long>, long[]> LongValues = AccessTools.FieldRefAccess<BinarySearchDictionary<int, long>, long[]>("m_values");
        private static readonly AccessTools.FieldRef<BinarySearchDictionary<int, long>, ushort> LongCount = AccessTools.FieldRefAccess<BinarySearchDictionary<int, long>, ushort>("m_length");
        private static readonly AccessTools.FieldRef<BinarySearchDictionary<int, float>, int[]> FloatKeys = AccessTools.FieldRefAccess<BinarySearchDictionary<int, float>, int[]>("m_keys");
        private static readonly AccessTools.FieldRef<BinarySearchDictionary<int, float>, float[]> FloatValues = AccessTools.FieldRefAccess<BinarySearchDictionary<int, float>, float[]>("m_values");
        private static readonly AccessTools.FieldRef<BinarySearchDictionary<int, float>, ushort> FloatCount = AccessTools.FieldRefAccess<BinarySearchDictionary<int, float>, ushort>("m_length");
        private static Dictionary<ZDOID, BinarySearchDictionary<int, long>> storedLongs;
        private static Dictionary<ZDOID, BinarySearchDictionary<int, float>> storedFloats;
        private static int pheromoneMost;
        private static readonly Dictionary<(ZDOID Id, int Key), ZDO> SpawnClocks = new();
        private static readonly Dictionary<(ZDOID Id, int Key), ZDO> SpawnSecondClocks = new();
        private static readonly Dictionary<int, NativeClockKeys> NativeClocks = new();
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> WorldObjects = AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<int, GameObject>> ScenePrefabs = AccessTools.FieldRefAccess<ZNetScene, Dictionary<int, GameObject>>("m_namedPrefabs");
        private static readonly List<(PersistentEventSystem System, PersistentEventSystem.ActivePersistentEvent Event, double Started)> PersistentClocks = new();
        private static bool spawningSuspended;
        private static readonly int NativeClockCycle = (Plugin.Id + ".sleep-clock-cycle").GetStableHashCode();
        private static readonly int NativeClockUntil = (Plugin.Id + ".sleep-clock-until").GetStableHashCode();

        private static bool EnemyPrefab(GameObject prefab)
        {
            if (!prefab || !prefab.GetComponent<MonsterAI>()) return false;
            var character = prefab.GetComponent<Character>();
            return character && character.m_faction != Character.Faction.Players && character.m_faction != Character.Faction.Dverger;
        }

        private static bool EnemySpawn(SpawnSystem.SpawnData spawn) => EnemyPrefab(spawn.m_prefab) ||
            spawn.m_huntPlayer && spawn.m_prefab && spawn.m_prefab.GetComponent<MonsterAI>();

        private static partial void InitializeSpawning(IntegrationPatches patches)
        {
            InitializeNativeSpawnRoutes(patches);
            InitializeModSpawning(patches);
            patches.Patch(AccessTools.Method(typeof(SpawnSystem), "UpdateSpawnList"),
                prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(ObserveSpawnClocks)),
                transpiler: new HarmonyMethod(typeof(SleepEnemyPause), nameof(SpawnEligibility)));
        }

        private static bool SpawnEnabled(SpawnSystem.SpawnData spawn) => spawn.m_enabled && (!Paused || !EnemySpawn(spawn));

        private static IEnumerable<CodeInstruction> SpawnEligibility(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var enabled = AccessTools.Field(typeof(SpawnSystem.SpawnData), nameof(SpawnSystem.SpawnData.m_enabled));
            var reads = code.Where(instruction => instruction.LoadsField(enabled)).ToArray();
            if (reads.Length != 1) throw new InvalidOperationException("The native spawn eligibility check changed.");
            // Keep the original list indices: they are part of the saved spawn-clock keys.
            reads[0].opcode = OpCodes.Call;
            reads[0].operand = AccessTools.Method(typeof(SleepEnemyPause), nameof(SpawnEnabled));
            return code;
        }

        private static void RememberClocks(SpawnSystem system, List<SpawnSystem.SpawnData> spawners, string salt)
        {
            var view = system.GetComponent<ZNetView>();
            if (!view || !view.IsValid() || spawners == null) return;
            var data = view.GetZDO();
            for (int index = 0; index < spawners.Count; index++)
            {
                var spawn = spawners[index];
                if (!EnemySpawn(spawn)) continue;
                int key = (salt + spawn.m_prefab.name + (index + 1)).GetStableHashCode();
                RememberTick(data, key);
            }
        }

        private static void RememberTick(ZDO data, int key)
        {
            long value = data.GetLong(key);
            if (value > 0 && !SpawnClocks.ContainsKey((data.m_uid, key))) SpawnClocks.Add((data.m_uid, key), data);
        }

        private static void Limit(Dictionary<int, double> keys, int key, double seconds) =>
            keys[key] = keys.TryGetValue(key, out double known) ? Math.Max(known, seconds) : seconds;

        // A zone rolls min(most at once, elapsed / interval) spawns. Night berserker entries use their own interval, and a
        // pheromone potion can raise most at once to its own limit while its drinker stands in the zone.
        private static double SpawnLimit(SpawnSystem.SpawnData spawn) =>
            (spawn.m_maxSpawned == 0 ? 1d : Math.Max(spawn.m_maxSpawned, pheromoneMost)) *
            (OrdinaryCreatureSpawns.Berserker(spawn) ? Math.Max(spawn.m_spawnInterval, OrdinaryCreatureSpawns.BerserkerInterval) : spawn.m_spawnInterval);

        private static void SpawnKeys(NativeClockKeys keys, List<SpawnSystem.SpawnData> spawners, string salt)
        {
            if (spawners == null) return;
            for (int index = 0; index < spawners.Count; index++)
                if (EnemySpawn(spawners[index])) Limit(keys.Ticks, (salt + spawners[index].m_prefab.name + (index + 1)).GetStableHashCode(), SpawnLimit(spawners[index]));
        }

        private static void CatalogNativeClocks()
        {
            NativeClocks.Clear();
            if (!ZNetScene.instance) return;
            pheromoneMost = ObjectDB.instance
                ? ObjectDB.instance.m_StatusEffects.OfType<SE_Stats>().Where(effect => effect.m_pheromoneTarget != null && !effect.m_pheromoneFlee)
                    .Select(effect => effect.m_pheromoneMaxInstanceOverride).DefaultIfEmpty(0).Max()
                : int.MaxValue;
            var alternatives = AltBiomeList.m_altBiomes.Distinct().ToArray();
            foreach (var entry in ScenePrefabs(ZNetScene.instance))
            {
                var prefab = entry.Value;
                if (!prefab) continue;
                var keys = new NativeClockKeys();
                var system = prefab.GetComponent<SpawnSystem>();
                if (system)
                {
                    foreach (var list in system.m_spawnLists) SpawnKeys(keys, list.m_spawners, "b_");
                    if (RandEventSystem.instance) foreach (var raid in RandEventSystem.instance.m_events) SpawnKeys(keys, raid.m_spawn, "e_");
                    // A heightmap's corner list is a union of alternative-biome definitions. Every possible
                    // index is below their total count, including sectors that are currently unloaded.
                    foreach (var alternative in alternatives)
                        for (int index = 0; index < alternatives.Length; index++) SpawnKeys(keys, alternative.m_spawn, "m" + index);
                }
                var creatureSpawner = prefab.GetComponent<CreatureSpawner>();
                // The Dyrnwyn champion's spawner returns him a day after he was last seen alive.
                if (creatureSpawner && EnemyProduct(creatureSpawner.m_creaturePrefab))
                    Limit(keys.Ticks, ZDOVars.s_aliveTime, Math.Max(0d, creatureSpawner.m_respawnTimeMinuts) * 60d);
                if (creatureSpawner && EnemyProduct(creatureSpawner.m_creaturePrefab) && DyrnwynFragments.ReturnsDaily(prefab.name))
                    Limit(keys.Ticks, ZDOVars.s_aliveTime, EnvMan.instance ? EnvMan.instance.m_dayLengthSec : double.PositiveInfinity);
                var trigger = prefab.GetComponent<TriggerSpawner>();
                if (trigger && trigger.m_creaturePrefabs.Any(EnemyProduct)) Limit(keys.Ticks, ZDOVars.s_spawnTime, Math.Max(0d, trigger.m_minSpawnInterval) * 60d);
                var egg = prefab.GetComponent<EggGrow>();
                if (egg && !egg.m_tamed && EnemyProduct(egg.m_grownPrefab)) Limit(keys.Seconds, ZDOVars.s_growStart, Math.Max(0d, egg.m_growTime));
                var character = prefab.GetComponent<Character>();
                if (character && prefab.GetComponent<MonsterAI>() && character.m_faction != Character.Faction.Players)
                {
                    keys.Character = true; keys.Neutral = character.m_faction == Character.Faction.Dverger;
                    // A wild creature's age is read when it grows up and by the fresh-spawn setup; its regeneration clock
                    // heals it fully once a full regeneration has passed.
                    var growup = prefab.GetComponent<Growup>();
                    Limit(keys.Ticks, ZDOVars.s_spawnTime, Math.Max(growup ? growup.m_growTime : 0d, FreshCreatureSpawns.ReplicaWindow / (double)TimeSpan.TicksPerSecond));
                    Limit(keys.Ticks, ZDOVars.s_worldTimeHash, Math.Max(0d, character.m_regenAllHPTime));
                    var tameable = prefab.GetComponent<Tameable>();
                    if (tameable) Limit(keys.Ticks, ZDOVars.s_tameLastFeeding, Math.Max(0d, tameable.m_fedDuration));
                }
                if (keys.Ticks.Count > 0 || keys.Seconds.Count > 0) NativeClocks.Add(entry.Key, keys);
            }
        }

        private static bool ClockOwner(ZDO data, out NativeClockKeys keys) =>
            NativeClocks.TryGetValue(data.GetPrefab(), out keys) &&
            !(keys.Character && (data.GetBool(ZDOVars.s_tamed) || keys.Neutral && !data.GetBool(ZDOVars.s_aggravated)));

        private static void RememberEntityClocks(ZDO data)
        {
            if (!spawningSuspended || data == null || !ClockOwner(data, out var keys)) return;
            if (keys.Ticks.Count > 0 && storedLongs != null && storedLongs.TryGetValue(data.m_uid, out var longs)) RememberTicks(data, keys, longs);
            if (keys.Seconds.Count > 0 && storedFloats != null && storedFloats.TryGetValue(data.m_uid, out var floats)) RememberSeconds(data, keys, floats);
        }

        private static void RememberTicks(ZDO data, NativeClockKeys keys, BinarySearchDictionary<int, long> longs)
        {
            int[] stored = LongKeys(longs); long[] values = LongValues(longs);
            for (int index = LongCount(longs) - 1; index >= 0; index--)
                if (values[index] > 0L && keys.Ticks.ContainsKey(stored[index]) && !SpawnClocks.ContainsKey((data.m_uid, stored[index])))
                    SpawnClocks.Add((data.m_uid, stored[index]), data);
        }

        private static void RememberSeconds(ZDO data, NativeClockKeys keys, BinarySearchDictionary<int, float> floats)
        {
            int[] stored = FloatKeys(floats); float[] values = FloatValues(floats);
            for (int index = FloatCount(floats) - 1; index >= 0; index--)
                if (values[index] > 0f && keys.Seconds.ContainsKey(stored[index]) && !SpawnSecondClocks.ContainsKey((data.m_uid, stored[index])))
                    SpawnSecondClocks.Add((data.m_uid, stored[index]), data);
        }

        // Only an object with stored long or float values can hold a clock, and about one in ten of the world's objects has any.
        private static void RememberWorldClocks()
        {
            if (ZDOMan.instance == null) return;
            var objects = WorldObjects(ZDOMan.instance);
            foreach (var entry in storedLongs)
                if (objects.TryGetValue(entry.Key, out var data) && ClockOwner(data, out var keys) && keys.Ticks.Count > 0) RememberTicks(data, keys, entry.Value);
            if (!NativeClocks.Values.Any(keys => keys.Seconds.Count > 0)) return;
            foreach (var entry in storedFloats)
                if (objects.TryGetValue(entry.Key, out var data) && ClockOwner(data, out var keys) && keys.Seconds.Count > 0) RememberSeconds(data, keys, entry.Value);
        }

        private static double TickLimit(ZDO data, int key) =>
            NativeClocks.TryGetValue(data.GetPrefab(), out var keys) && keys.Ticks.TryGetValue(key, out double limit) ? limit : double.PositiveInfinity;

        private static double SecondLimit(ZDO data, int key) =>
            NativeClocks.TryGetValue(data.GetPrefab(), out var keys) && keys.Seconds.TryGetValue(key, out double limit) ? limit : double.PositiveInfinity;

        private static bool Settled(double ageSeconds, double limitSeconds) => ageSeconds >= limitSeconds + SettledMarginSeconds;

        private static void ObserveSpawnClocks(SpawnSystem __instance, List<SpawnSystem.SpawnData> spawners, string groupSalt)
        {
            if (Paused) RememberClocks(__instance, spawners, groupSalt);
        }

        private static partial void SuspendSpawning()
        {
            spawningSuspended = true;
            SuspendSpawnRoutes();
            SpawnClocks.Clear(); SpawnSecondClocks.Clear(); PersistentClocks.Clear();
            CaptureModClocks();
            CatalogNativeClocks();
            storedLongs = (Dictionary<ZDOID, BinarySearchDictionary<int, long>>)StoredLongsField.GetValue(null);
            storedFloats = (Dictionary<ZDOID, BinarySearchDictionary<int, float>>)StoredFloatsField.GetValue(null);
            RememberWorldClocks();
            foreach (var system in UnityEngine.Object.FindObjectsByType<SpawnSystem>(FindObjectsSortMode.None))
            {
                foreach (var list in system.m_spawnLists) RememberClocks(system, list.m_spawners, "b_");
                if (RandEventSystem.instance) RememberClocks(system, RandEventSystem.instance.GetCurrentSpawners(), "e_");
            }
            foreach (var system in UnityEngine.Object.FindObjectsByType<PersistentEventSystem>(FindObjectsSortMode.None))
                foreach (var active in system.m_activePersistentEvents.list)
                    if (active.Source.objectsToSpawn.Any(PersistentEnemies)) PersistentClocks.Add((system, active, active.startTime));
        }

        private static partial void ResumeSpawning(long sleepCycle, long started, long stopped)
        {
            if (!spawningSuspended) return;
            long elapsed = Math.Max(0L, stopped - started);
            var adjusted = new HashSet<ZDO>();
            long Remaining(ZDO data) => Math.Max(0L, stopped - Math.Max(started,
                data.GetLong(NativeClockCycle) == sleepCycle ? data.GetLong(NativeClockUntil) : started));
            foreach (var clock in SpawnClocks)
            {
                var data = clock.Value;
                if (ReferenceEquals(ZDOMan.instance.GetZDO(clock.Key.Id), data) &&
                    (data.IsOwner() || ZNet.instance.IsServer() && !data.HasOwner()))
                {
                    long current = data.GetLong(clock.Key.Key);
                    if (current > 0L)
                    {
                        long shifted = current + Math.Min(Remaining(data), Math.Max(0L, stopped - current));
                        if (Settled((stopped - shifted) / (double)TimeSpan.TicksPerSecond, TickLimit(data, clock.Key.Key))) continue;
                        data.Set(clock.Key.Key, shifted);
                    }
                    adjusted.Add(data);
                }
            }
            double seconds = elapsed / (double)TimeSpan.TicksPerSecond, stoppedSeconds = stopped / (double)TimeSpan.TicksPerSecond;
            foreach (var clock in SpawnSecondClocks)
            {
                var data = clock.Value;
                if (ReferenceEquals(ZDOMan.instance.GetZDO(clock.Key.Id), data) &&
                    (data.IsOwner() || ZNet.instance.IsServer() && !data.HasOwner()))
                {
                    float current = data.GetFloat(clock.Key.Key);
                    if (current > 0f)
                    {
                        float shifted = current + (float)Math.Min(Remaining(data) / (double)TimeSpan.TicksPerSecond, Math.Max(0d, stoppedSeconds - current));
                        if (Settled(stoppedSeconds - shifted, SecondLimit(data, clock.Key.Key))) continue;
                        data.Set(clock.Key.Key, shifted);
                    }
                    adjusted.Add(data);
                }
            }
            foreach (var data in adjusted) { data.Set(NativeClockCycle, sleepCycle); data.Set(NativeClockUntil, stopped); }
            foreach (var clock in PersistentClocks)
                if (clock.System && clock.System.m_activePersistentEvents.list.Contains(clock.Event)) clock.Event.startTime = clock.Started + seconds;
            ResumeModClocks(sleepCycle, started, stopped);
            ResumeSpawnRoutes();
            ClearSpawning();
        }

        private static partial void ClearSpawning()
        { SpawnClocks.Clear(); SpawnSecondClocks.Clear(); PersistentClocks.Clear(); NativeClocks.Clear(); storedLongs = null; storedFloats = null; spawningSuspended = false; }
    }
}
