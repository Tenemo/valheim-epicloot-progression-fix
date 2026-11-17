using System;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A sap extractor's owner credits elapsed world time against its root's sap. Natively it spends that time before it
    // has found a root that may still be loading, judges the root by a level whose regeneration has not been applied
    // yet, and drains up to the whole batch from the root even when only a few units fit, so neighbours sharing the root
    // miss their catch-up. Here the time waits while the extractor's area is still loading, the root's regeneration is
    // brought up to date before it is judged or drained, and the root loses exactly the units stored. Production speed,
    // capacity, regeneration and extraction are native; an extractor with no root in a loaded area keeps no time, and a
    // full extractor discards it as before.
    [HarmonyPatch]
    internal static class SapCollectorProduction
    {
        private static readonly AccessTools.FieldRef<SapCollector, ZNetView> View = AccessTools.FieldRefAccess<SapCollector, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<SapCollector, ResourceRoot> Root = AccessTools.FieldRefAccess<SapCollector, ResourceRoot>("m_root");
        private static readonly AccessTools.FieldRef<ResourceRoot, ZNetView> RootView = AccessTools.FieldRefAccess<ResourceRoot, ZNetView>("m_nview");
        private static readonly Action<SapCollector> UpdateEffects = AccessTools.MethodDelegate<Action<SapCollector>>(AccessTools.Method(typeof(SapCollector), "UpdateEffects"));
        private static readonly Func<ResourceRoot, double> RootElapsed = AccessTools.MethodDelegate<Func<ResourceRoot, double>>(AccessTools.Method(typeof(ResourceRoot), "GetTimeSinceLastUpdate"));
        private static readonly Action<ResourceRoot, float> ModifyLevel = AccessTools.MethodDelegate<Action<ResourceRoot, float>>(AccessTools.Method(typeof(ResourceRoot), "ModifyLevel"));

        [HarmonyPatch(typeof(SapCollector), "UpdateTick")]
        private static class Production
        {
            private static bool Prefix(SapCollector __instance)
            {
                var view = View(__instance);
                ref var root = ref Root(__instance);
                if (__instance.m_mustConnectTo && !root)
                {
                    foreach (var collider in Physics.OverlapSphere(__instance.transform.position, .2f))
                        if (collider.GetComponentInParent<ResourceRoot>() is ResourceRoot found) { root = found; break; }
                }
                if (view.IsOwner()) Produce(__instance, view.GetZDO(), root);
                UpdateEffects(__instance);
                return false;
            }
        }

        private static void Produce(SapCollector collector, ZDO zdo, ResourceRoot root)
        {
            // A root that may still be loading keeps the elapsed time for the next tick.
            if (collector.m_mustConnectTo && !root && !ZNetScene.instance.IsAreaReady(collector.transform.position)) return;
            var now = ZNet.instance.GetTime();
            double elapsed = Math.Max(0.0, (now - new DateTime(zdo.GetLong(ZDOVars.s_lastTime, now.Ticks))).TotalSeconds);
            zdo.Set(ZDOVars.s_lastTime, now.Ticks);
            int level = zdo.GetInt(ZDOVars.s_level);
            float supply = root ? Supply(root, now) : 0f;
            if (level >= collector.m_maxLevel || !root || supply <= 1f) return;
            float product = zdo.GetFloat(ZDOVars.s_product) + (float)elapsed;
            if (product > collector.m_secPerUnit)
            {
                int produced = (int)(product / collector.m_secPerUnit);
                // The root drains only while it holds more than the amount, as natively.
                int stored = Math.Min(Math.Min(produced, collector.m_maxLevel - level), (int)Math.Ceiling(supply) - 1);
                if (stored > 0)
                {
                    zdo.Set(ZDOVars.s_level, level + stored);
                    // Sent straight to the root's owner, which applies regeneration first; the native local check would
                    // judge a level that is still missing it.
                    RootView(root).InvokeRPC("RPC_Drain", (float)stored);
                }
                product = 0f;
            }
            zdo.Set(ZDOVars.s_product, product);
        }

        // The root's level with the regeneration it has earned since its owner last applied it.
        private static float Supply(ResourceRoot root, DateTime now)
        {
            var zdo = RootView(root).GetZDO();
            double since = Math.Max(0.0, (now - new DateTime(zdo.GetLong(ZDOVars.s_lastTime, now.Ticks))).TotalSeconds);
            return Mathf.Clamp(root.GetLevel() + (float)(root.m_regenPerSec * since), 0f, root.m_maxLevel);
        }

        // The root's owner applies its pending regeneration before judging a drain, so a drain arriving before the root's
        // own tick after loading sees its real level.
        [HarmonyPatch(typeof(ResourceRoot), "RPC_Drain")]
        private static class Drain
        {
            private static void Prefix(ResourceRoot __instance)
            {
                var view = RootView(__instance);
                if (view.IsOwner()) ModifyLevel(__instance, (float)(__instance.m_regenPerSec * RootElapsed(__instance)));
            }
        }
    }
}
