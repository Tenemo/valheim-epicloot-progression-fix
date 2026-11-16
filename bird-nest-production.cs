using System.Collections.Generic;
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

        // Called for an owned nest or hive whose production step is due, with the biome and space check the game's update
        // has just made.
        internal static bool MayProduce(ZNetView view, Beehive hive, bool biomeAndSpace)
        {
            var zdo = view.GetZDO();
            if (zdo.GetPrefab() != Nest) return true;
            if (!AreaReady(hive.transform.position)) return false;
            if (biomeAndSpace) return true;
            zdo.Set(ZDOVars.s_lastTime, ZNet.instance.GetTime().Ticks);
            return false;
        }

        // IsAreaReady visits every object in the 3 by 3 zones around a point, and the nests a base loaded together all step
        // in one batch of the game's delayed calls. Within one batch, identified by its frame and fixed step, a zone's
        // answer is kept for the nests after the first; it never outlives the batch.
        private static readonly Dictionary<Vector2s, bool> BatchAnswers = new Dictionary<Vector2s, bool>();
        private static int batchFrame = -1;
        private static bool batchFixedStep;
        private static double batchFixedTime;

        internal static bool AreaReady(Vector3 position)
        {
            int frame = Time.frameCount;
            bool fixedStep = Time.inFixedTimeStep;
            double fixedTime = Time.fixedTimeAsDouble;
            if (frame != batchFrame || fixedStep != batchFixedStep || fixedTime != batchFixedTime)
            {
                BatchAnswers.Clear();
                batchFrame = frame; batchFixedStep = fixedStep; batchFixedTime = fixedTime;
            }
            var zone = ZoneSystem.GetZone(position);
            if (!BatchAnswers.TryGetValue(zone, out bool ready)) BatchAnswers[zone] = ready = ZNetScene.instance.IsAreaReady(position);
            return ready;
        }
    }
}
