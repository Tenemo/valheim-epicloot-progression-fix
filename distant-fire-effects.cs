using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Flames remain visible until their building unloads; their surface illumination has a separate distant fade.
    [HarmonyPatch(typeof(Fireplace), "Start")]
    internal static class DistantFireEffects
    {
        private static void Postfix(Fireplace __instance) => KeepMeshes(__instance);

        private static void KeepMeshes(Fireplace fire)
        {
            if (fire == null || fire.GetComponent<Piece>() == null || ZNet.instance != null && ZNet.instance.IsDedicated()) return;
            LastLod.Keep(fire);
            DistantTorchFlames.Apply(fire);
            var view = fire.GetComponent<ZNetView>();
            if (view && view.IsValid())
                foreach (var light in fire.GetComponentsInChildren<Light>(true))
                    if (light.type == LightType.Point) SurfaceLighting.Attach(light);
        }

        // Some torches load their effects asynchronously after Fireplace.Start.
        [HarmonyPatch(typeof(SoftReferencePrefabSpawner), "SpawnPrefab")]
        private static class LoadedEffects
        {
            private static void Postfix(SoftReferencePrefabSpawner __instance) =>
                KeepMeshes(__instance.GetComponentInParent<Fireplace>());
        }

    }
}
