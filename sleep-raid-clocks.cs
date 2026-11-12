using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static partial class SleepEnemyPause
    {
        private const string RaidClockCycle = Plugin.Id + ".sleep-raid-clock-cycle";
        private const string RaidClockUntil = Plugin.Id + ".sleep-raid-clock-until";
        private static readonly HashSet<string> RaidClockKeys = new(StringComparer.Ordinal)
            { "SLS_RAID_START", "SLS_RAID_SPAWN_GEN_START", "SLS_RAID_WINDDOWN", "SLS_RAID_SPAWNS_ACTIVE" };
        private static readonly Dictionary<ZDOID, ZDO> RaidClockRecords = new();
        private static readonly HashSet<int> RaidPrefabs = new();
        private static Type raidDoubleProperty, raidListProperty;
        private static FieldInfo raidDoubleView, raidListView;
        private static PropertyInfo raidDoubleKey, raidListKey, raidNextSpawn;
        private static MethodInfo raidDoubleSet, raidListSet;
        [ThreadStatic] private static bool applyingRaidOffset;

        private static string RaidOffset(string key) => Plugin.Id + ".sleep-offset." + key;
        private static string RaidWritten(string key) => Plugin.Id + ".sleep-written." + key;

        private static void InitializeRaidClocks(IntegrationPatches patches)
        {
            var data = StarLevelSystem.Assembly.GetType("StarLevelSystem.common.DataObjects", true);
            raidDoubleProperty = data.GetNestedType("DoubleZNetProperty");
            raidListProperty = data.GetNestedType("RaidMonitorListZNetProperty");
            raidDoubleView = AccessTools.Field(raidDoubleProperty, "zNetView");
            raidListView = AccessTools.Field(raidListProperty, "zNetView");
            raidDoubleKey = AccessTools.Property(raidDoubleProperty, "Key"); raidListKey = AccessTools.Property(raidListProperty, "Key");
            raidDoubleSet = AccessTools.Method(raidDoubleProperty, "Set"); raidListSet = AccessTools.Method(raidListProperty, "Set");
            raidNextSpawn = AccessTools.Property(data.GetNestedType("RaidMonitor"), "NextSpawn");
            if (raidDoubleView?.FieldType != typeof(ZNetView) || raidListView?.FieldType != typeof(ZNetView) || raidNextSpawn?.PropertyType != typeof(double))
                throw new InvalidOperationException("StarLevelSystem's raid-clock properties changed.");
            patches.Patch(AccessTools.Method(raidDoubleProperty, "Get"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(ReadRaidTime)));
            patches.Patch(AccessTools.Method(raidListProperty, "Get"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(ReadRaidSpawns)));
            foreach (var type in new[] { raidDoubleProperty, raidListProperty })
                patches.Patch(AccessTools.Method(type, "SetValue"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(RaidTimeWritten)));
        }

        private static bool RaidProperty(object property, out ZNetView view, out string key)
        {
            bool scalar = raidDoubleProperty.IsInstanceOfType(property);
            view = (ZNetView)(scalar ? raidDoubleView : raidListView).GetValue(property);
            key = (string)(scalar ? raidDoubleKey : raidListKey).GetValue(property);
            return view && view.IsValid() && RaidClockKeys.Contains(key);
        }

        private static void StoreAdjustedRaidTime(object property, object value, MethodInfo write)
        {
            bool previous = applyingRaidOffset;
            try { applyingRaidOffset = true; write.Invoke(property, new[] { value }); }
            finally { applyingRaidOffset = previous; }
        }

        private static void ReadRaidTime(object __instance, ref double __result)
        {
            if (!RaidProperty(__instance, out var view, out string key)) return;
            long ticks = view.GetZDO().GetLong(RaidOffset(key));
            if (ticks == 0) return;
            if (__result > 0d) __result += ticks / (double)TimeSpan.TicksPerSecond;
            if (view.IsOwner()) StoreAdjustedRaidTime(__instance, __result, raidDoubleSet);
        }

        private static void ReadRaidSpawns(object __instance, object __result)
        {
            if (!RaidProperty(__instance, out var view, out string key)) return;
            long ticks = view.GetZDO().GetLong(RaidOffset(key));
            if (ticks == 0) return;
            foreach (var spawn in (IEnumerable)__result)
            {
                double due = (double)raidNextSpawn.GetValue(spawn);
                if (due > 0d) raidNextSpawn.SetValue(spawn, due + ticks / (double)TimeSpan.TicksPerSecond);
            }
            if (view.IsOwner()) StoreAdjustedRaidTime(__instance, __result, raidListSet);
        }

        private static void RaidTimeWritten(object __instance)
        {
            if (!RaidProperty(__instance, out var view, out string key) || !view.IsOwner()) return;
            var data = view.GetZDO();
            data.Set(RaidOffset(key), 0L);
            if (!applyingRaidOffset && Active) data.Set(RaidWritten(key), ZNet.instance.GetTime().Ticks);
        }

        private static void CaptureRaidClocks()
        {
            RaidClockRecords.Clear(); RaidPrefabs.Clear();
            if (!ZNetScene.instance) return;
            foreach (var prefab in ScenePrefabs(ZNetScene.instance)) if (prefab.Value && prefab.Value.GetComponent(raidRunnerType)) RaidPrefabs.Add(prefab.Key);
            foreach (var data in WorldObjects(ZDOMan.instance).Values) RememberRaidClock(data);
        }

        private static void RememberRaidClock(ZDO data)
        {
            if (spawningSuspended && data != null && RaidPrefabs.Contains(data.GetPrefab())) RaidClockRecords[data.m_uid] = data;
        }

        private static void ResumeRaidClocks(long sleepCycle, long started, long stopped)
        {
            foreach (var entry in RaidClockRecords)
            {
                var data = entry.Value;
                if (!ReferenceEquals(ZDOMan.instance.GetZDO(entry.Key), data) ||
                    !ZNet.instance.IsServer() && !data.IsOwner()) continue;
                long accountedUntil = data.GetLong(RaidClockCycle) == sleepCycle ? data.GetLong(RaidClockUntil) : started;
                foreach (string key in RaidClockKeys)
                {
                    if (data.GetByteArray(key) == null) continue;
                    long held = Math.Max(0L, stopped - Math.Max(Math.Max(started, accountedUntil), data.GetLong(RaidWritten(key))));
                    data.Set(RaidOffset(key), data.GetLong(RaidOffset(key)) + held);
                }
                data.Set(RaidClockCycle, sleepCycle); data.Set(RaidClockUntil, stopped);
            }
            RaidClockRecords.Clear();
        }

        private static void ClearRaidClocks() { RaidClockRecords.Clear(); RaidPrefabs.Clear(); applyingRaidOffset = false; }
    }
}
