using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Creatures from night-only spawn rules carry a mark that only their AI acts on, and AI runs only on a player's game. One
    // nobody was simulating at sunrise stays frozen in the save until a player's game loads it by day and the AI deletes it in
    // front of them. When day starts, the server deletes these in slices, so the walk over the world costs no noticeable frame.
    internal static class NightSpawnCleanup
    {
        private const string Feature = "Sunrise cleanup of night creatures";
        private static readonly long FrameBudget = Stopwatch.Frequency / 1000;
        private static ManualLogSource logger;
        private static AccessTools.FieldRef<ZDOMan, List<ZDO>[]> sectors;
        private static bool day, walking;
        private static int next, removed, frames;
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

        // Only a connected player simulates a creature: a dedicated server runs no AI out in the world, and the game keeps a
        // departed player as the owner of what they left behind until another player comes near it.
        private static bool Leftover(ZDO data)
        {
            if (!data.Persistent || !CreaturePrefabs.IsCreature(data) || !data.GetBool(ZDOVars.s_despawnInDay)) return false;
            long owner = data.GetOwner();
            return owner == 0L || ZNet.instance.GetPeer(owner) == null;
        }

        private static void Step(ZDOMan __instance)
        {
            if (!ZNet.instance.IsDedicated()) return;
            bool wasDay = day;
            day = EnvMan.IsDay();
            if (!day) { walking = false; return; }
            if (!wasDay)
            {
                walking = true;
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
                    if (!Leftover(data)) continue;
                    data.SetOwner(ZDOMan.GetSessionID());
                    __instance.DestroyZDO(data);
                    removed++;
                }
                long now = Stopwatch.GetTimestamp();
                if (now - frameStart < FrameBudget) continue;
                if (now - frameStart > worstFrame) worstFrame = now - frameStart;
                return;
            }
            walking = false;
            long end = Stopwatch.GetTimestamp();
            if (end - frameStart > worstFrame) worstFrame = end - frameStart;
            logger.LogInfo($"Sunrise cleanup removed {removed} night creatures in {Milliseconds(end - started):F0} ms over {frames} frames, worst frame {Milliseconds(worstFrame):F2} ms.");
        }

        private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    }
}
