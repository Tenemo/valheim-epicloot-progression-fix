using System;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A bird nest's owner judges its cover only once everything around it has loaded: a nest whose neighbours are still
    // loading would otherwise pass the space check against missing blockers and credit all the time since its last step.
    // While its area loads, a due step waits with time, progress and feathers untouched. Once loaded, a blocked nest or
    // one in the wrong biome records that interval as spent, so removing a blocker later cannot credit the blocked time;
    // its partial progress and stored feathers stay. Eligible nests and ordinary beehives produce natively.
    internal static class BirdNestProduction
    {
        private static readonly int Nest = "piece_birdnest".GetStableHashCode();
        private static readonly Func<Beehive, bool> CheckBiome = AccessTools.MethodDelegate<Func<Beehive, bool>>(AccessTools.Method(typeof(Beehive), "CheckBiome"));
        private static readonly Func<Beehive, bool> HaveFreeSpace = AccessTools.MethodDelegate<Func<Beehive, bool>>(AccessTools.Method(typeof(Beehive), "HaveFreeSpace"));

        // Called for an owned nest or hive whose production step is due.
        internal static bool MayProduce(ZNetView view, Beehive hive)
        {
            var zdo = view.GetZDO();
            if (zdo.GetPrefab() != Nest) return true;
            if (!ZNetScene.instance.IsAreaReady(hive.transform.position)) return false;
            if (CheckBiome(hive) && HaveFreeSpace(hive)) return true;
            zdo.Set(ZDOVars.s_lastTime, ZNet.instance.GetTime().Ticks);
            return false;
        }
    }
}
