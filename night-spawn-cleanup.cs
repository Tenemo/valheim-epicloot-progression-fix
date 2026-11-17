using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Creatures from night-only spawn rules carry a mark that only their AI acts on: by day the AI deletes one with no player
    // within 40 m and leads one nearer off until it gets there, so a player meets night creatures by day that vanish in front of
    // them. One nobody was simulating at sunrise stays frozen until a player's game loads it by day. When day starts, the server
    // deletes every marked creature, whoever's game runs it, in slices, so the walk over the world costs no noticeable frame. A
    // second pass catches creatures a player's game created just before its own sunrise that reached the server during the first.
    internal static class NightSpawnCleanup
    {
        private const string Feature = "Sunrise cleanup of night creatures";
        private static readonly long FrameBudget = Stopwatch.Frequency / 1000;
        private static ManualLogSource logger;
        private static AccessTools.FieldRef<ZDOMan, List<ZDO>[]> sectors;
        private static bool day, walking;
        private static int next, pass, removed, frames;
        private static long started, worstFrame;

        internal static void Initialize(IntegrationPatches patches, ManualLogSource log)
        {
            logger = log;
            var update = AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.Update));
            var records = AccessTools.Field(typeof(ZDOMan), "m_objectsBySector");
            if (update == null || records?.FieldType != typeof(List<ZDO>[]))
            {
                DisabledFeatures.Set(Feature, "the server's world records moved");
                return;
            }
            sectors = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>(records);
            patches.Patch(update, postfix: new HarmonyMethod(typeof(NightSpawnCleanup), nameof(Step)));
        }

        // Feeding a hungry creature to tame it clears the mark, so tames and creatures being tamed stay.
        private static bool Marked(ZDO data) => data.Persistent && CreaturePrefabs.IsCreature(data) && data.GetBool(ZDOVars.s_despawnInDay);

        private static void Step(ZDOMan __instance)
        {
            if (!ZNet.instance.IsDedicated()) return;
            bool wasDay = day;
            day = EnvMan.IsDay();
            if (!day) { walking = false; return; }
            if (!wasDay)
            {
                walking = true; pass = 1;
                next = removed = frames = 0; worstFrame = 0L;
                started = Stopwatch.GetTimestamp();
            }
            if (!walking) return;
            long frameStart = Stopwatch.GetTimestamp();
            var lists = sectors(__instance);
            frames++;
            while (next < lists.Length)
            {
                var list = lists[next++];
                if (list == null) continue;
                foreach (var data in list)
                {
                    if (!Marked(data)) continue;
                    data.SetOwner(ZDOMan.GetSessionID());
                    __instance.DestroyZDO(data);
                    removed++;
                }
                long now = Stopwatch.GetTimestamp();
                if (now - frameStart < FrameBudget) continue;
                if (now - frameStart > worstFrame) worstFrame = now - frameStart;
                return;
            }
            long end = Stopwatch.GetTimestamp();
            if (end - frameStart > worstFrame) worstFrame = end - frameStart;
            if (pass++ == 1) { next = 0; return; }
            walking = false;
            logger.LogInfo($"Sunrise cleanup removed {removed} night creatures in {Milliseconds(end - started):F0} ms over {frames} frames, worst frame {Milliseconds(worstFrame):F2} ms.");
        }

        private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    }
}
