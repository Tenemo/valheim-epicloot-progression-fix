using System;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Floating caches trigger contacts and applies its water buoyancy offset to the tar pickup check. Confirm a
    // refusal against the current liquid instead: dry cells still return a surface half a meter below sampled ground.
    [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.InTar))]
    internal static class TarPickup
    {
        private static readonly int LiquidLayers = LayerMask.GetMask("WaterVolume");
        private static readonly AccessTools.FieldRef<LiquidSurface, LiquidVolume> VolumeOf =
            AccessTools.FieldRefAccess<LiquidSurface, LiquidVolume>("m_liquid");
        private static readonly Func<LiquidVolume, Vector3, Vector2> WorldToLocal =
            AccessTools.MethodDelegate<Func<LiquidVolume, Vector3, Vector2>>(AccessTools.Method(typeof(LiquidVolume), "WorldToLocal"));
        private static readonly Func<LiquidVolume, float, float, float> GetDepth =
            AccessTools.MethodDelegate<Func<LiquidVolume, float, float, float>>(AccessTools.Method(typeof(LiquidVolume), "GetDepth", new[] { typeof(float), typeof(float) }));

        private static void Postfix(Rigidbody ___m_body, ref bool __result)
        {
            if (!__result) return;
            Vector3 position = ___m_body.worldCenterOfMass;
            foreach (var collider in Physics.OverlapSphere(position, 0f, LiquidLayers, QueryTriggerInteraction.Collide))
            {
                var water = collider.GetComponent<WaterVolume>();
                if (water)
                {
                    if (water.GetLiquidType() == LiquidType.Tar && position.y < water.GetWaterSurface(position)) return;
                    continue;
                }
                var surface = collider.GetComponent<LiquidSurface>();
                if (!surface || surface.GetLiquidType() != LiquidType.Tar) continue;
                var volume = VolumeOf(surface);
                Vector2 local = WorldToLocal(volume, position);
                if (GetDepth(volume, local.x, local.y) > 0.001f && position.y < surface.GetSurface(position)) return;
            }
            __result = false;
        }
    }
}
