using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static partial class SleepEnemyPause
    {
        // Unloaded spawners have clocks too. Track recent objects on the dedicated server and
        // retain every seconds-based clock (a float can round towards the expiry boundary).
        private static class ActiveSpawnClocks
        {
            private static readonly Dictionary<int, NativeClockKeys> catalog = new();
            private static readonly HashSet<int> watched = new();
            private static readonly HashSet<ZDOID> active = new(), dirty = new(), changedDuringSleep = new();
            private static Dictionary<ZDOID, BinarySearchDictionary<int, long>> longs;
            private static Dictionary<ZDOID, BinarySearchDictionary<int, float>> floats;
            private static ZDOMan world;
            private static ZDOID[] longSeed, floatSeed;
            private static int cursor;
            private static long lastTime;
            private static bool indexedSleep, reseedAfterWake;
            internal static bool Installed { get; private set; }
            internal static int IndexedSleeps { get; private set; }

            internal static void Install(IntegrationPatches patches)
            {
                // The client assembly returns a constant false, including a batch client. Avoid
                // detouring its hot ZDO setters. Other builds use the runtime dedicated check.
                var role = AccessTools.Method(typeof(ZNet), nameof(ZNet.IsDedicated)).GetMethodBody()?.GetILAsByteArray();
                if (role != null && role.Length == 2 && role[0] == OpCodes.Ldc_I4_0.Value && role[1] == OpCodes.Ret.Value) return;
                void Hook(Type type, string name, Type[] args, string before, string after) =>
                    patches.Patch(AccessTools.Method(type, name, args),
                        prefix: before == null ? null : new HarmonyMethod(typeof(ActiveSpawnClocks), before) { priority = Priority.First },
                        postfix: after == null ? null : new HarmonyMethod(typeof(ActiveSpawnClocks), after));
                foreach (Type type in new[] { typeof(long), typeof(float), typeof(int) })
                {
                    Hook(typeof(ZDOExtraData), "Set", new[] { typeof(ZDOID), typeof(int), type }, nameof(BeforeValue), nameof(AfterValue));
                    string remove = type == typeof(long) ? "RemoveLong" : type == typeof(float) ? "RemoveFloat" : "RemoveInt";
                    Hook(typeof(ZDOExtraData), remove, new[] { typeof(ZDOID), typeof(int) }, nameof(BeforeValue), nameof(AfterValue));
                }
                Hook(typeof(ZDO), "Set", new[] { typeof(int), typeof(long) }, nameof(BeforeLocal), null);
                Hook(typeof(ZDO), "Set", new[] { typeof(int), typeof(float) }, nameof(BeforeLocal), null);
                Hook(typeof(ZDO), "Deserialize", null, nameof(BeforeRecord), nameof(AfterRecord));
                Hook(typeof(ZDO), "SetPrefab", null, nameof(BeforeRecord), nameof(AfterRecord));
                Hook(typeof(ZDO), "Reset", null, nameof(Removed), null);
                Hook(typeof(ZDOMan), "LoadChunks", null, nameof(Clear), null);
                Installed = true;
            }

            internal static void Clear()
            {
                world = null;
                longs = null;
                floats = null;
                longSeed = floatSeed = null;
                catalog.Clear();
                watched.Clear();
                active.Clear();
                dirty.Clear();
                changedDuringSleep.Clear();
                indexedSleep = false;
                reseedAfterWake = false;
                lastTime = 0;
            }

            private static void BeforeValue(ZDOID __0, int __1)
            {
                if (indexedSleep && watched.Contains(__1)) RememberBeforeChange(__0);
            }
            private static void BeforeLocal(ZDO __instance, int __0) => BeforeValue(__instance.m_uid, __0);
            private static void BeforeRecord(ZDO __instance)
            {
                if (indexedSleep) RememberBeforeChange(__instance.m_uid);
            }
            private static void AfterValue(ZDOID __0, int __1, bool __result)
            {
                if (longs != null && __result && watched.Contains(__1)) dirty.Add(__0);
            }
            private static void AfterRecord(ZDO __instance)
            {
                if (longs != null && (active.Contains(__instance.m_uid) || catalog.ContainsKey(__instance.GetPrefab())))
                    dirty.Add(__instance.m_uid);
            }
            private static void Removed(ZDO __instance) => active.Remove(__instance.m_uid);

            // A settled clock can be removed or refreshed during sleep. Enroll what existed
            // before the first change so the native wake rule can still write the cycle marker.
            private static void RememberBeforeChange(ZDOID id)
            {
                if (changedDuringSleep.Add(id)) RememberEntityClocks(world.GetZDO(id));
            }

            private static bool Same(Dictionary<int, double> left, Dictionary<int, double> right) =>
                left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

            private static bool SameCatalog() => catalog.Count == NativeClocks.Count && NativeClocks.All(pair =>
                catalog.TryGetValue(pair.Key, out var keys) && keys.Character == pair.Value.Character &&
                keys.Neutral == pair.Value.Neutral && Same(keys.Ticks, pair.Value.Ticks) && Same(keys.Seconds, pair.Value.Seconds));

            private static void Seed()
            {
                active.Clear();
                dirty.Clear();
                watched.Clear();
                watched.Add(ZDOVars.s_tamed);
                watched.Add(ZDOVars.s_aggravated);
                foreach (var keys in catalog.Values)
                {
                    watched.UnionWith(keys.Ticks.Keys);
                    watched.UnionWith(keys.Seconds.Keys);
                }
                longSeed = longs.Keys.ToArray();
                floatSeed = catalog.Values.Any(keys => keys.Seconds.Count > 0) ? floats.Keys.ToArray() : Array.Empty<ZDOID>();
                cursor = 0;
            }

            private static bool HasActiveClock(ZDOID id, long now)
            {
                var data = world.GetZDO(id);
                if (data == null || !ClockOwner(data, catalog, out var keys)) return false;
                if (keys.Ticks.Count != 0 && longs.TryGetValue(id, out var ticks))
                {
                    var names = LongKeys(ticks);
                    var values = LongValues(ticks);
                    for (int i = 0; i < LongCount(ticks); i++)
                        if (values[i] > 0 && keys.Ticks.TryGetValue(names[i], out var limit) &&
                            !Settled((now - values[i]) / (double)TimeSpan.TicksPerSecond, limit)) return true;
                }
                if (keys.Seconds.Count != 0 && floats.TryGetValue(id, out var seconds))
                {
                    var names = FloatKeys(seconds);
                    var values = FloatValues(seconds);
                    for (int i = 0; i < FloatCount(seconds); i++)
                        if (values[i] > 0 && keys.Seconds.ContainsKey(names[i])) return true;
                }
                return false;
            }

            private static void Refresh(ZDOID id, long now)
            {
                if (HasActiveClock(id, now)) active.Add(id);
                else active.Remove(id);
            }
            private static void Flush(long now)
            {
                foreach (var id in dirty) Refresh(id, now);
                dirty.Clear();
            }

            internal static void Update()
            {
                if (!Installed || !ZNet.instance.IsDedicated() || ZDOMan.instance == null || !ZNetScene.instance || !ObjectDB.instance) return;
                long now = ZNet.instance.GetTime().Ticks;
                if (!ReferenceEquals(world, ZDOMan.instance))
                {
                    if (spawningSuspended) return;
                    Clear();
                    world = ZDOMan.instance;
                    longs = (Dictionary<ZDOID, BinarySearchDictionary<int, long>>)StoredLongsField.GetValue(null);
                    floats = (Dictionary<ZDOID, BinarySearchDictionary<int, float>>)StoredFloatsField.GetValue(null);
                    BuildClockCatalog(catalog);
                    Seed();
                }
                else if (now < lastTime)
                {
                    if (indexedSleep) reseedAfterWake = true;
                    else Seed();
                }
                lastTime = now;
                long selectionTime = indexedSleep ? worldStarted : now;
                if (longSeed != null)
                {
                    long start = Stopwatch.GetTimestamp();
                    int length = longSeed.Length + floatSeed.Length;
                    while (cursor < length)
                    {
                        Refresh(cursor < longSeed.Length ? longSeed[cursor] : floatSeed[cursor - longSeed.Length], selectionTime);
                        if ((++cursor & 31) == 0 && Stopwatch.GetTimestamp() - start >= Stopwatch.Frequency * 0.0015) break;
                    }
                    if (cursor == length) longSeed = floatSeed = null;
                }
                Flush(selectionTime);
            }

            internal static bool Select()
            {
                if (longs == null || !ReferenceEquals(world, ZDOMan.instance)) return false;
                if (!ReferenceEquals(longs, storedLongs) || !ReferenceEquals(floats, storedFloats) ||
                    worldStarted < lastTime || !SameCatalog())
                {
                    longs = storedLongs;
                    floats = storedFloats;
                    catalog.Clear();
                    foreach (var pair in NativeClocks) catalog.Add(pair.Key, pair.Value);
                    Seed();
                }
                if (longSeed != null) return false;
                Flush(worldStarted);
                active.RemoveWhere(id => !HasActiveClock(id, worldStarted));
                foreach (var id in active) RememberEntityClocks(world.GetZDO(id));
                changedDuringSleep.Clear();
                indexedSleep = true;
                IndexedSleeps++;
                return true;
            }

            internal static void Finish(long started, long stopped)
            {
                if (!indexedSleep) return;
                indexedSleep = false;
                // A rewind inside the transition can make a previously settled clock fresh.
                // Touched objects have their original enrollment already; new clocks on them
                // must not be enrolled retroactively.
                if (stopped < started)
                {
                    foreach (var id in longs.Keys)
                        if (!changedDuringSleep.Contains(id)) RememberEntityClocks(world.GetZDO(id));
                    if (catalog.Values.Any(keys => keys.Seconds.Count != 0))
                        foreach (var id in floats.Keys)
                            if (!changedDuringSleep.Contains(id)) RememberEntityClocks(world.GetZDO(id));
                    reseedAfterWake = true;
                }
                if (reseedAfterWake) { Seed(); reseedAfterWake = false; }
                changedDuringSleep.Clear();
            }
        }
    }
}
