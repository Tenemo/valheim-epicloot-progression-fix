using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Floating), "TerrainCheck")]
    internal static class CartTerrainRecovery
    {
        private static void Prefix(Floating __instance, out Vector3 __state) => __state = __instance.transform.position;

        private static void Postfix(Floating __instance, Vector3 __state)
        {
            // Native recovery moves the transform, leaving the chassis and wheel bodies at their buried positions.
            // Apply the complete assembly's move before physics or interpolation can restore the old pose.
            if (__instance.transform.position != __state && __instance.GetComponent<Vagon>()) Physics.SyncTransforms();
        }
    }
}
