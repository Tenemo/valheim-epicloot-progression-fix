using System;
using System.Collections;
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
        private sealed class SpawnSchedule
        {
            internal MonoBehaviour Source;
            internal string Method;
            internal float Due, Repeat, Remaining;
            internal bool Paused;
        }
        private static readonly Dictionary<(MonoBehaviour Source, string Method), SpawnSchedule> SpawnSchedules = new();
        private static float nextSchedulePrune;
        private static readonly FieldInfo WaitDuration = AccessTools.Field(typeof(WaitForSeconds), "m_Seconds");
        private static readonly FieldInfo EventPrefabs = AccessTools.Field(typeof(PersistentEventSystem.ObjectSpawnSettings), "prefabs");

        private static bool EnemyProduct(GameObject prefab)
        {
            if (!prefab) return false;
            if (EnemyPrefab(prefab)) return true;
            var ability = prefab.GetComponent<SpawnAbility>();
            if (ability && ability.m_spawnPrefab.Any(EnemyPrefab)) return true;
            // The installed Jotun invasion creates FimbulLocation01, whose children contain its enemy spawner.
            return prefab.GetComponentsInChildren<SpawnArea>(true).Any(area => area.m_prefabs.Any(entry => EnemyPrefab(entry.m_prefab)));
        }

        private static bool EnemyProducer(MonoBehaviour source)
        {
            if (!source) return false;
            switch (source)
            {
                case CreatureSpawner spawner: return EnemyProduct(spawner.m_creaturePrefab);
                case SpawnArea area: return area.m_prefabs.Any(entry => EnemyProduct(entry.m_prefab));
                case TriggerSpawner trigger: return trigger.m_creaturePrefabs.Any(EnemyProduct);
                case SpawnPrefab spawn: return EnemyProduct(spawn.m_prefab);
                case SpawnOnDamaged damaged: return EnemyProduct(damaged.m_spawnOnDamage);
                case EggHatch hatch: return EnemyProduct(hatch.m_spawnPrefab);
                case EggGrow egg: return !egg.m_tamed && EnemyProduct(egg.m_grownPrefab);
                case Growup growth:
                    var character = growth.GetComponent<Character>();
                    return (!growth.m_inheritTame || !character || !character.IsTamed()) &&
                        (EnemyProduct(growth.m_grownPrefab) || growth.m_altGrownPrefabs != null && growth.m_altGrownPrefabs.Any(entry => EnemyProduct(entry.m_prefab)));
                case OfferingBowl bowl: return EnemyProduct(bowl.m_bossPrefab);
                case SpawnAbility ability: return EnemyAttack(ability) || ability.m_spawnPrefab.Any(EnemyProduct);
                default: return GlobalSpawnProducers.Contains(source.GetType()) || EnemyComponent(source);
            }
        }

        private static void InitializeNativeSpawnRoutes(IntegrationPatches patches)
        {
            var gate = new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowSpawnProcedure)) { priority = Priority.First };
            foreach (var entry in new[] {
                (typeof(CreatureSpawner), "UpdateSpawner"), (typeof(CreatureSpawner), "Spawn"),
                (typeof(SpawnArea), "UpdateSpawn"), (typeof(SpawnArea), "SpawnOne"),
                (typeof(TriggerSpawner), "TrySpawning"), (typeof(TriggerSpawner), "Spawn"),
                (typeof(SpawnPrefab), "TrySpawn"), (typeof(SpawnOnDamaged), "OnDamaged"),
                (typeof(EggHatch), "CheckSpawn"), (typeof(EggHatch), "Hatch"), (typeof(EggGrow), "GrowUpdate"), (typeof(Growup), "GrowUpdate"),
                (typeof(OfferingBowl), "Interact"), (typeof(OfferingBowl), "UseItem"), (typeof(OfferingBowl), "RPC_SpawnBoss"), (typeof(OfferingBowl), "DelayedSpawnBoss"),
                (typeof(DropProjectileOverDistance), "FixedUpdate"), (typeof(DropProjectileOverDistance), "SpawnProjectile"),
                (typeof(TimedDestruction), "DestroyNow"),
                (typeof(Tameable), "Update"), (typeof(Tameable), "TamingUpdate") })
                patches.Patch(AccessTools.Method(entry.Item1, entry.Item2), prefix: gate);
            patches.Patch(AccessTools.Method(typeof(CreatureSpawner.Group), nameof(CreatureSpawner.Group.SpawnWeighted)),
                prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowSpawnGroup)) { priority = Priority.First });
            foreach (string method in new[] { "FixedUpdate", "StartRandomEvent" })
                patches.Patch(AccessTools.Method(typeof(RandEventSystem), method), prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowEnemyEvent)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(typeof(SE_Spawn), nameof(SE_Spawn.UpdateStatusEffect)),
                prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowSpawnEffect)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(typeof(SpawnAbility), "Spawn"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(WaitForSpawnAbility)));
            // Setup starts the coroutine synchronously, so its source must be known before the first step.
            patches.Patch(AccessTools.Method(typeof(SpawnAbility), nameof(SpawnAbility.Setup)), prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(RememberAttack)));
            patches.Patch(AccessTools.Method(typeof(Attack), nameof(Attack.Start)),
                prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowSpawnAttack)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(typeof(PersistentEventSystem), "RPC_RequestStartEvent"),
                prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowPersistentEvent)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(typeof(PersistentEventSystem.ObjectSpawnSettings), "Create"),
                prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowPersistentObjects)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(typeof(PersistentEventSystem), "Update"), transpiler: new HarmonyMethod(typeof(SleepEnemyPause), nameof(PersistentEventClock)));
            foreach (var entry in new[] { (typeof(CreatureSpawner), "Awake"), (typeof(SpawnArea), "Awake"), (typeof(SpawnPrefab), "Start"),
                (typeof(EggHatch), "Start"), (typeof(EggGrow), "Start"), (typeof(Growup), "Start"), (typeof(OfferingBowl), "SpawnBoss"), (typeof(Tameable), "Awake") })
                patches.Patch(AccessTools.Method(entry.Item1, entry.Item2), transpiler: new HarmonyMethod(typeof(SleepEnemyPause), nameof(TrackSpawnInvokes)));
            patches.Patch(AccessTools.Method(typeof(OfferingBowl), "IsBossSpawnQueued"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(BossStillQueued)));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(TimedDestruction)).Where(method => method.Name == "Trigger"))
                patches.Patch(method, transpiler: new HarmonyMethod(typeof(SleepEnemyPause), nameof(TrackSpawnInvokes)));
        }

        private static bool AllowSpawnProcedure(MonoBehaviour __instance, MethodBase __originalMethod)
        {
            string method = __originalMethod.Name;
            if (Paused && EnemyProducer(__instance))
            {
                var view = __instance.GetComponentInParent<ZNetView>();
                if (view && view.IsValid()) { RememberEntityClocks(view.GetZDO()); RememberRaidClock(view.GetZDO()); }
                if (SpawnSchedules.TryGetValue((__instance, method), out var pending)) PauseSchedule(pending);
                return false;
            }
            if (method != "Update" && method != "FixedUpdate" && SpawnSchedules.TryGetValue((__instance, method), out var schedule))
            {
                if (schedule.Repeat > 0f) schedule.Due = Time.time + schedule.Repeat;
                else SpawnSchedules.Remove((__instance, method));
            }
            return true;
        }

        private static bool AllowSpawnGroup(CreatureSpawner.Group __instance) => !Paused || !__instance.Any(spawner => EnemyProduct(spawner.m_creaturePrefab));
        private static bool AllowEnemyEvent() => !Paused;
        private static bool AllowSpawnEffect(SE_Spawn __instance) => !Paused || !Hostile(EffectOwner(__instance)) && !EnemyProduct(__instance.m_prefab);
        private static bool AllowSpawnAttack(Attack __instance, Humanoid character) => !Paused || !Hostile(character) && !EnemyProduct(__instance.m_attackProjectile);
        private static bool PersistentEnemies(PersistentEventSystem.ObjectSpawnSettings settings) => ((List<GameObject>)EventPrefabs.GetValue(settings)).Any(EnemyProduct);
        private static bool AllowPersistentObjects(PersistentEventSystem.ObjectSpawnSettings __instance) => !Paused || !PersistentEnemies(__instance);
        private static bool AllowPersistentEvent(PersistentEventSystem __instance, int sourceEventId) =>
            !Paused || !__instance.m_possibleEvents[sourceEventId].objectsToSpawn.Any(PersistentEnemies);

        private static double PausedEventStart(PersistentEventSystem.ActivePersistentEvent active) => active.startTime +
            (Paused && active.Source.objectsToSpawn.Any(PersistentEnemies) ? Math.Max(0d, ZNet.instance.GetTimeSeconds() - worldStarted / (double)TimeSpan.TicksPerSecond) : 0d);

        private static IEnumerable<CodeInstruction> PersistentEventClock(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var start = AccessTools.Field(typeof(PersistentEventSystem.ActivePersistentEvent), nameof(PersistentEventSystem.ActivePersistentEvent.startTime));
            var reads = code.Where(instruction => instruction.LoadsField(start)).ToArray();
            if (reads.Length != 1) throw new InvalidOperationException("The native persistent-event expiration check changed.");
            reads[0].opcode = OpCodes.Call; reads[0].operand = AccessTools.Method(typeof(SleepEnemyPause), nameof(PausedEventStart));
            return code;
        }

        private static void WaitForSpawnAbility(SpawnAbility __instance, ref IEnumerator __result)
        { if (EnemyProducer(__instance)) __result = HoldSpawnRoutine(__result, () => __instance && EnemyProducer(__instance)); }

        private static IEnumerator HoldSpawnRoutine(IEnumerator routine, Func<bool> enemy)
        {
            var network = ZNet.instance;
            try
            {
                while (true)
                {
                    while (Paused && enemy() && network == ZNet.instance) yield return null;
                    if (network && network != ZNet.instance) yield break;
                    if (!routine.MoveNext()) yield break;
                    if (routine.Current is WaitForSeconds wait)
                    {
                        float remaining = (float)WaitDuration.GetValue(wait);
                        while (remaining > 0f) { yield return null; if (!Paused || !enemy()) remaining -= Time.deltaTime; }
                    }
                    else if (routine.Current is WaitForSecondsRealtime realtime)
                    {
                        float remaining = realtime.waitTime, previous = Time.realtimeSinceStartup;
                        while (remaining > 0f)
                        {
                            yield return null;
                            float now = Time.realtimeSinceStartup;
                            if (!Paused || !enemy()) remaining -= now - previous;
                            previous = now;
                        }
                    }
                    else if (routine.Current is IEnumerator nested) yield return HoldSpawnRoutine(nested, enemy);
                    else yield return routine.Current;
                }
            }
            finally { (routine as IDisposable)?.Dispose(); }
        }

        private static IEnumerable<CodeInstruction> TrackSpawnInvokes(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList(); int replaced = 0;
            var once = AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.Invoke), new[] { typeof(string), typeof(float) });
            var repeating = AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.InvokeRepeating));
            var querying = AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.IsInvoking), new[] { typeof(string) });
            foreach (var instruction in code)
            {
                if (instruction.Calls(querying))
                {
                    instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(SleepEnemyPause), nameof(SpawnIsInvoking));
                    continue;
                }
                if (!instruction.Calls(once) && !instruction.Calls(repeating)) continue;
                string replacement = instruction.Calls(once) ? nameof(ScheduleSpawnOnce) : nameof(ScheduleSpawnRepeating);
                instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(SleepEnemyPause), replacement); replaced++;
            }
            if (replaced == 0) throw new InvalidOperationException("A native spawn invocation schedule changed.");
            return code;
        }

        private static void ScheduleSpawnOnce(MonoBehaviour source, string method, float delay) => ScheduleSpawn(source, method, delay, 0f);
        private static void ScheduleSpawnRepeating(MonoBehaviour source, string method, float delay, float repeat) => ScheduleSpawn(source, method, delay, repeat);
        private static bool SpawnIsInvoking(MonoBehaviour source, string method) =>
            SpawnSchedules.TryGetValue((source, method), out var schedule) && schedule.Paused || source.IsInvoking(method);
        private static void ScheduleSpawn(MonoBehaviour source, string method, float delay, float repeat)
        {
            if (Time.time >= nextSchedulePrune)
            {
                nextSchedulePrune = Time.time + 10f;
                foreach (var entry in SpawnSchedules.ToArray())
                    if (!entry.Value.Source || !entry.Value.Paused && !entry.Value.Source.IsInvoking(entry.Value.Method)) SpawnSchedules.Remove(entry.Key);
            }
            if (Game.instance && !Game.instance.IsShuttingDown() && (source is TimedDestruction || EnemyProducer(source)))
            {
                var schedule = new SpawnSchedule { Source = source, Method = method, Due = Time.time + delay, Repeat = repeat, Remaining = delay, Paused = Paused && EnemyProducer(source) };
                SpawnSchedules[(source, method)] = schedule;
                if (schedule.Paused) return;
            }
            if (repeat > 0f) source.InvokeRepeating(method, delay, repeat);
            else source.Invoke(method, delay);
        }

        private static void PauseSchedule(SpawnSchedule schedule)
        {
            if (schedule.Paused || !schedule.Source || !schedule.Source.IsInvoking(schedule.Method)) return;
            schedule.Remaining = Mathf.Max(0f, schedule.Due - Time.time);
            schedule.Source.CancelInvoke(schedule.Method);
            schedule.Paused = true;
        }

        private static void SuspendSpawnRoutes()
        {
            foreach (var schedule in SpawnSchedules.Values) if (schedule.Source && EnemyProducer(schedule.Source)) PauseSchedule(schedule);
        }

        private static void PauseObjectSchedules(GameObject instance)
        {
            foreach (var schedule in SpawnSchedules.Values)
                if (schedule.Source && (schedule.Source.gameObject == instance || schedule.Source.transform.IsChildOf(instance.transform)) && EnemyProducer(schedule.Source))
                    PauseSchedule(schedule);
        }

        private static void ResumeSpawnRoutes()
        {
            foreach (var entry in SpawnSchedules.ToArray())
            {
                var schedule = entry.Value;
                if (!schedule.Source) { SpawnSchedules.Remove(entry.Key); continue; }
                if (!schedule.Paused) continue;
                schedule.Paused = false; schedule.Due = Time.time + schedule.Remaining;
                if (schedule.Repeat > 0f) schedule.Source.InvokeRepeating(schedule.Method, schedule.Remaining, schedule.Repeat);
                else schedule.Source.Invoke(schedule.Method, schedule.Remaining);
            }
        }

        private static void BossStillQueued(OfferingBowl __instance, ref bool __result)
        {
            if (SpawnSchedules.TryGetValue((__instance, "DelayedSpawnBoss"), out var schedule) && schedule.Paused) __result = true;
        }

        private static void ClearSpawnRoutes() { SpawnSchedules.Clear(); nextSchedulePrune = 0f; }
    }
}
