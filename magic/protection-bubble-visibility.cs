using System;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The bubble's sphere draws before the ordinary transparent queue and writes no depth, so every mist particle that
    // overlaps it on screen, also one behind it, is painted over it. Draw it after the mist, as the flames are. The copy
    // leaves the material's other users alone, and the prefab keeps it across later world loads.
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    internal static class ProtectionBubbleVisibility
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ZNetScene __instance)
        {
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) return;
            var prefab = __instance.GetPrefab("vfx_StaffShield");
            var sphere = prefab ? prefab.GetComponentInChildren<MeshRenderer>(true) : null;
            var material = sphere ? sphere.sharedMaterial : null;
            if (!material) throw new InvalidOperationException("Staff of Protection's bubble is missing.");
            if (material.renderQueue != FlameVisibility.Queue)
                sphere.sharedMaterial = new Material(material) { name = material.name, renderQueue = FlameVisibility.Queue };
        }
    }
}
