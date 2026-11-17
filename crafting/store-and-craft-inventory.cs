using System;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // EpicLoot asks its providers when a panel opens or a selection changes. StoreAndCraft's five-second nearby cache
    // can still describe the previous chests then, leaving a correctly stocked table disabled until it is reopened.
    internal static class StoreAndCraftInventory
    {
        private static Action<Vector3, float> rescan;
        private static Action invalidateCounts;
        private static int refreshedFrame = -1;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.StoreAndCraft, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var nearby = assembly.GetType("StoreAndCraft.NearbyIndex", true);
            rescan = AccessTools.MethodDelegate<Action<Vector3, float>>(AccessTools.Method(nearby, "ForceRescanAt", new[] { typeof(Vector3), typeof(float) }));
            invalidateCounts = AccessTools.MethodDelegate<Action>(AccessTools.Method(nearby, "InvalidateCounts"));
            var bridge = assembly.GetType("StoreAndCraft.EpicLootBridge", true);
            foreach (string method in new[] { "GetNearbyItems", "CountNearbyItem" })
                harmony.Patch(AccessTools.Method(bridge, method), prefix: new HarmonyMethod(typeof(StoreAndCraftInventory), nameof(Refresh)));
        }

        private static void Refresh()
        {
            var player = Player.m_localPlayer;
            if (!player || refreshedFrame == Time.frameCount) return;
            refreshedFrame = Time.frameCount;
            // Native scanning retains range, chest settings and ward checks; native providers still count and pay.
            rescan(player.transform.position, -1f);
            invalidateCounts();
        }
    }
}
