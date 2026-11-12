using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static partial class SleepEnemyPause
    {
        private sealed class NativeClockKeys
        {
            internal readonly HashSet<int> Ticks = new(), Seconds = new();
            internal bool Character, Neutral;
        }
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

        private static void RememberSeconds(ZDO data, int key)
        {
            float value = data.GetFloat(key);
            if (value > 0f && !SpawnSecondClocks.ContainsKey((data.m_uid, key))) SpawnSecondClocks.Add((data.m_uid, key), data);
        }

        private static void SpawnKeys(NativeClockKeys keys, List<SpawnSystem.SpawnData> spawners, string salt)
        {
            if (spawners == null) return;
            for (int index = 0; index < spawners.Count; index++)
                if (EnemySpawn(spawners[index])) keys.Ticks.Add((salt + spawners[index].m_prefab.name + (index + 1)).GetStableHashCode());
        }

        private static void CatalogNativeClocks()
        {
            NativeClocks.Clear();
            if (!ZNetScene.instance) return;
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
                if (creatureSpawner && EnemyProduct(creatureSpawner.m_creaturePrefab)) keys.Ticks.Add(ZDOVars.s_aliveTime);
                var trigger = prefab.GetComponent<TriggerSpawner>();
                if (trigger && trigger.m_creaturePrefabs.Any(EnemyProduct)) keys.Ticks.Add(ZDOVars.s_spawnTime);
                var egg = prefab.GetComponent<EggGrow>();
                if (egg && !egg.m_tamed && EnemyProduct(egg.m_grownPrefab)) keys.Seconds.Add(ZDOVars.s_growStart);
                var character = prefab.GetComponent<Character>();
                if (character && prefab.GetComponent<MonsterAI>() && character.m_faction != Character.Faction.Players)
                {
                    keys.Character = true; keys.Neutral = character.m_faction == Character.Faction.Dverger;
                    keys.Ticks.Add(ZDOVars.s_spawnTime); keys.Ticks.Add(ZDOVars.s_worldTimeHash);
                    if (prefab.GetComponent<Tameable>()) keys.Ticks.Add(ZDOVars.s_tameLastFeeding);
                }
                if (keys.Ticks.Count > 0 || keys.Seconds.Count > 0) NativeClocks.Add(entry.Key, keys);
            }
        }

        private static void RememberEntityClocks(ZDO data)
        {
            if (!spawningSuspended || data == null || !NativeClocks.TryGetValue(data.GetPrefab(), out var keys) ||
                keys.Character && (data.GetBool(ZDOVars.s_tamed) || keys.Neutral && !data.GetBool(ZDOVars.s_aggravated))) return;
            foreach (int key in keys.Ticks) RememberTick(data, key);
            foreach (int key in keys.Seconds) RememberSeconds(data, key);
        }

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
            if (ZDOMan.instance != null) foreach (var data in WorldObjects(ZDOMan.instance).Values) RememberEntityClocks(data);
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
                    if (current > 0L) data.Set(clock.Key.Key, current + Math.Min(Remaining(data), Math.Max(0L, stopped - current)));
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
                    if (current > 0f) data.Set(clock.Key.Key, current + (float)Math.Min(Remaining(data) / (double)TimeSpan.TicksPerSecond, Math.Max(0d, stoppedSeconds - current)));
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
        { SpawnClocks.Clear(); SpawnSecondClocks.Clear(); PersistentClocks.Clear(); NativeClocks.Clear(); spawningSuspended = false; }
    }
}
