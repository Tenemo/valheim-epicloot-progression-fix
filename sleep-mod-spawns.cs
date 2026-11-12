using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static partial class SleepEnemyPause
    {
        private static readonly HashSet<Type> GlobalSpawnProducers = new();
        private static readonly Dictionary<ZDOID, Coroutine> DeferredMultipliers = new();
        private static readonly List<Action<double, float>> ModClocks = new();
        private static Type raidControlType, raidRunnerType, remoteManagerType, nemesisManagerType, bountyControllerType;
        private static MethodInfo multiplySpawn, creatureCache, accrueRaidTime;
        private static FieldInfo raidAccrualTime;
        private static float modGameStarted, unscaledStarted, pausedUnscaledSeconds;
        private static bool accruingBoundary;
        private static ConfigEntry<bool> useVanillaRaids;

        private static void InitializeModSpawning(IntegrationPatches patches)
        {
            var gate = new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowEnemyEvent)) { priority = Priority.First };
            var producer = new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowSpawnProcedure)) { priority = Priority.First };
            var routine = new HarmonyMethod(typeof(SleepEnemyPause), nameof(HoldEnemyRoutine));
            var epic = typeof(EpicLoot.EpicLoot).Assembly;
            bountyControllerType = epic.GetType("EpicLoot.Adventure.AdventureSpawnController", true);
            patches.Patch(AccessTools.Method(bountyControllerType, "Update"), prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowBounty)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(bountyControllerType, "SpawnBountyTargets"), prefix: gate);
            patches.Patch(AccessTools.Method(bountyControllerType, "DeterminespawnPoint"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(HoldBountyRoutine)));
            var searchIterator = bountyControllerType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Single(type => type.Name.Contains("<DeterminespawnPoint>"));
            patches.Patch(AccessTools.Method(searchIterator, "MoveNext"), transpiler: new HarmonyMethod(typeof(SleepEnemyPause), nameof(BountySearchClock)));
            if (!StarLevelSystem.Installed) return;
            var assembly = StarLevelSystem.Assembly;
            Type Type(string name) => assembly.GetType(name, true);
            var nemesis = Type("StarLevelSystem.modules.NemesisSystem.NemesisActions");
            remoteManagerType = Type("StarLevelSystem.modules.NemesisSystem.NemesisRemoteSpawnManager");
            var remoteControl = Type("StarLevelSystem.modules.NemesisSystem.NemesisRemoteSpawnControl");
            var remoteSpawner = Type("StarLevelSystem.modules.NemesisSystem.NemesisRemoteSpawner");
            var raidManager = Type("StarLevelSystem.modules.Raids.RaidManager");
            raidRunnerType = Type("StarLevelSystem.modules.Raids.RaidRunner");
            raidControlType = Type("StarLevelSystem.modules.Raids.RaidControl");
            useVanillaRaids = (ConfigEntry<bool>)AccessTools.Field(Type("StarLevelSystem.common.ValConfig"), "UseVanillaRaidConfiguration").GetValue(null);
            nemesisManagerType = Type("StarLevelSystem.modules.NemesisSystem.NemesisManager");
            var summoner = Type("StarLevelSystem.Modifiers.Summoner+SLSSummoner");
            var splitter = Type("StarLevelSystem.Modifiers.Splitter+CharacterOnDeath");
            // This installed prefix creates the creature itself, even when another prefix skips native Hatch.
            patches.Patch(AccessTools.Method(Type("StarLevelSystem.modules.LevelSystem.LevelPatches+EggHatchSpawnPatch"), "Prefix"),
                prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowModEggHatch)) { priority = Priority.First });
            foreach (var type in new[] { remoteManagerType, remoteSpawner, raidManager, raidRunnerType }) GlobalSpawnProducers.Add(type);
            patches.Patch(AccessTools.Method(nemesis, "NemesisRandomSpawner"), prefix: gate);
            foreach (var entry in new[] { (remoteManagerType, "CheckRemoteSpawns"), (remoteSpawner, "Update"), (remoteSpawner, "SpawnAndDestroy"),
                (raidManager, "CheckForRaidUpdate"), (raidRunnerType, "Update"), (summoner, "SpawnCreaturesBatch"), (summoner, "SpawnCreatureRandomly") })
                patches.Patch(AccessTools.Method(entry.Item1, entry.Item2), prefix: producer);
            foreach (var entry in new[] { (remoteManagerType, "ForceSpawnForBiome"), (raidManager, "RunRaidCheck"),
                (raidControlType, "DispatchForcedRaid"), (raidControlType, "StartNetworkedRaidForPeer") })
                patches.Patch(AccessTools.Method(entry.Item1, entry.Item2), prefix: gate);
            foreach (var entry in new[] { (remoteManagerType, "RunSpawnCycleAsync"), (remoteManagerType, "ScoutAndPlace"),
                (remoteControl, "ScoutBiomeLocation"), (remoteControl, "ReconcileFromSpawnerZDOs") })
                patches.Patch(AccessTools.Method(entry.Item1, entry.Item2), postfix: routine);
            patches.Patch(AccessTools.Method(splitter, "Prefix"), prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowReactiveDeath)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(splitter, "SpawnSplitsAsync"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(HoldSplits)));
            foreach (var entry in new[] { (remoteManagerType, "Awake"), (raidManager, "Awake"), (summoner, "SetupSummoner") })
                patches.Patch(AccessTools.Method(entry.Item1, entry.Item2), transpiler: new HarmonyMethod(typeof(SleepEnemyPause), nameof(TrackSpawnInvokes)));

            var spawnrate = Type("StarLevelSystem.modules.Spawnrate");
            multiplySpawn = AccessTools.Method(spawnrate, "CheckSetApplySpawnrate");
            creatureCache = AccessTools.Method(Type("StarLevelSystem.Data.CompositeLazyCache"), "GetCacheEntry", new[] { typeof(Character) });
            patches.Patch(multiplySpawn, prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(DeferMultiplier)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(spawnrate, "DestroyCoroutine"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(HoldRemoval)));
            accrueRaidTime = AccessTools.Method(raidControlType, "AccruePlayerTime");
            raidAccrualTime = AccessTools.Field(raidControlType, "lastPlayTimeAccrual");
            patches.Patch(accrueRaidTime, prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowRaidAccrual)));
            patches.Patch(AccessTools.PropertyGetter(raidRunnerType, "SecondsSinceStart"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(RaidAge)));
            InitializeRaidClocks(patches);
            InitializeModifierPauses(patches);
        }

        private static bool IsBounty(MonoBehaviour controller)
        {
            var view = controller ? controller.GetComponent<ZNetView>() : null;
            return view && view.IsValid() && view.GetZDO().GetBool("isBounty");
        }
        private static bool AllowBounty(MonoBehaviour __instance) => !Paused || !IsBounty(__instance);
        private static void HoldBountyRoutine(MonoBehaviour __instance, ref IEnumerator __result)
        { if (IsBounty(__instance)) __result = HoldSpawnRoutine(__result, () => IsBounty(__instance)); }
        private static void HoldEnemyRoutine(ref IEnumerator __result) => __result = HoldSpawnRoutine(__result, () => true);
        private static bool AllowReactiveDeath(Character __0) => !Paused || !Hostile(__0);
        private static bool AllowModEggHatch(EggHatch __0, ref bool __result)
        {
            if (!Paused || !EnemyProducer(__0)) return true;
            __result = false;
            return false;
        }
        private static void HoldSplits(GameObject prefab, bool shouldTame, ref IEnumerator __result)
        { if (!shouldTame && EnemyPrefab(prefab)) __result = HoldSpawnRoutine(__result, () => true); }
        private static void HoldRemoval(GameObject go, ref IEnumerator __result)
        { if (go && Hostile(go.GetComponent<Character>())) __result = HoldSpawnRoutine(__result, () => true); }

        private static bool DeferMultiplier(Character __0)
        {
            if (!Paused || !Hostile(__0)) return true;
            var id = __0.GetZDOID();
            if (!DeferredMultipliers.ContainsKey(id))
            {
                DeferredMultipliers.Add(id, null);
                DeferredMultipliers[id] = runner.StartCoroutine(FinishMultiplier(__0, id, ZNet.instance));
            }
            return false;
        }

        private static IEnumerator FinishMultiplier(Character character, ZDOID id, ZNet network)
        {
            try
            {
                while (Paused && network == ZNet.instance) yield return null;
                if (!character || network != ZNet.instance || !character.IsOwner()) yield break;
                var cache = creatureCache.Invoke(null, new object[] { character });
                if (cache != null) multiplySpawn.Invoke(null, new[] { (object)character, cache });
            }
            finally { DeferredMultipliers.Remove(id); }
        }

        private static IEnumerable<CodeInstruction> BountySearchClock(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            var clock = AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTime));
            var controller = __originalMethod.DeclaringType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(field => field.FieldType == bountyControllerType);
            int count = 0;
            for (int index = 0; index < code.Count; index++)
            {
                if (!code[index].Calls(clock)) continue;
                var load = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code[index]);
                code.InsertRange(index, new[] { load, new CodeInstruction(OpCodes.Ldfld, controller) });
                index += 2; code[index].opcode = OpCodes.Call;
                code[index].operand = AccessTools.Method(typeof(SleepEnemyPause), nameof(BountyUnscaledTime)); count++;
            }
            if (count != 2) throw new InvalidOperationException("EpicLoot's bounty search deadline changed.");
            return code;
        }

        private static float BountyUnscaledTime(MonoBehaviour controller) => IsBounty(controller)
            ? (Active ? unscaledStarted : Time.unscaledTime) - pausedUnscaledSeconds : Time.unscaledTime;

        private static bool AllowRaidAccrual() => !Paused || accruingBoundary;
        private static void RaidAge(ref double __result)
        {
            if (Active) __result = Math.Max(0d, __result - Math.Max(0d, ZNet.instance.GetTimeSeconds() - worldStarted / (double)TimeSpan.TicksPerSecond));
        }

        private static void RememberNumber(object target, MemberInfo member, bool worldClock)
        {
            object Read() => member is FieldInfo field ? field.GetValue(target) : ((PropertyInfo)member).GetValue(target);
            void Write(object value) { if (member is FieldInfo field) field.SetValue(target, value); else ((PropertyInfo)member).SetValue(target, value); }
            object original = Read();
            if (Convert.ToDouble(original) <= 0d) return;
            ModClocks.Add((world, game) =>
            {
                if (target is UnityEngine.Object unity && !unity || !Equals(Read(), original)) return;
                double value = Convert.ToDouble(original) + (worldClock ? world : game);
                Write(original is float ? (object)(float)value : value);
            });
        }

        private static void CaptureModClocks()
        {
            modGameStarted = Time.time; unscaledStarted = Time.unscaledTime; ModClocks.Clear();
            foreach (MonoBehaviour controller in UnityEngine.Object.FindObjectsByType(bountyControllerType, FindObjectsSortMode.None))
                if (IsBounty(controller)) foreach (string field in new[] { "nextSearchTime", "searchStartTime", "areaReadyTime" })
                    RememberNumber(controller, AccessTools.Field(bountyControllerType, field), false);
            if (!StarLevelSystem.Installed) return;
            foreach (var spec in new[] { (remoteManagerType, "nextCheckTime", true), (nemesisManagerType, "nextNemeisActionTime", true),
                (raidRunnerType, "Endtime", true), (raidRunnerType, "nextDefeatCheck", false) })
            {
                if (spec.Item1 == raidRunnerType && useVanillaRaids.Value) continue;
                foreach (var instance in UnityEngine.Object.FindObjectsByType(spec.Item1, FindObjectsSortMode.None))
                    RememberNumber(instance, AccessTools.Field(spec.Item1, spec.Item2), spec.Item3);
            }
            if (!useVanillaRaids.Value) CaptureRaidClocks();
            CaptureModifierClocks();
            if (!ZNet.instance.IsServer() || useVanillaRaids.Value) return;
            AccessTools.Method(raidControlType, "EnsureRegistryLoaded").Invoke(null, null);
            try { accruingBoundary = true; accrueRaidTime.Invoke(null, null); }
            finally { accruingBoundary = false; }
            RememberNumber(null, AccessTools.Property(raidControlType, "NextRaidCheckTime"), true);
            if (AccessTools.Property(raidControlType, "StampedClock").GetValue(null).ToString() == "WorldTime")
            {
                var players = (IDictionary)AccessTools.Property(raidControlType, "ServerPlayerRaidData").GetValue(null);
                foreach (var player in players.Values)
                {
                    RememberNumber(player, AccessTools.Property(player.GetType(), "NextRaidableTime"), true);
                    var history = (IDictionary)AccessTools.Property(player.GetType(), "LastRaidByName").GetValue(player);
                    foreach (var key in history.Keys.Cast<object>().ToArray())
                    {
                        double original = (double)history[key];
                        ModClocks.Add((world, game) => { if (history.Contains(key) && (double)history[key] == original) history[key] = original + world; });
                    }
                }
            }
        }

        private static void ResumeModClocks(long sleepCycle, long started, long stopped)
        {
            double world = Math.Max(0L, stopped - started) / (double)TimeSpan.TicksPerSecond;
            float game = Time.time - modGameStarted;
            foreach (var restore in ModClocks) restore(world, game);
            pausedUnscaledSeconds += Time.unscaledTime - unscaledStarted;
            ModClocks.Clear();
            if (!StarLevelSystem.Installed) return;
            if (useVanillaRaids.Value) return;
            ResumeRaidClocks(sleepCycle, started, stopped);
            foreach (var instance in UnityEngine.Object.FindObjectsByType(raidRunnerType, FindObjectsSortMode.None))
            {
                AccessTools.Field(raidRunnerType, "cachedDataRevision").SetValue(instance, uint.MaxValue);
                var spawners = AccessTools.Field(raidRunnerType, "RaidSpawners");
                spawners.SetValue(instance, Activator.CreateInstance(spawners.FieldType));
            }
            if (ZNet.instance.IsServer())
            {
                raidAccrualTime.SetValue(null, ZNet.instance.GetTimeSeconds());
                AccessTools.Method(raidControlType, "MarkPlayerRaidDataDirty").Invoke(null, null);
            }
        }

        private static void ClearModSpawns()
        {
            foreach (var routine in DeferredMultipliers.Values.ToArray()) if (routine != null) runner.StopCoroutine(routine);
            DeferredMultipliers.Clear(); ModClocks.Clear(); pausedUnscaledSeconds = 0f;
            ClearRaidClocks(); ClearModifierClocks();
        }
    }
}
