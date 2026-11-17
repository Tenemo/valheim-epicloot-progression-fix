using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A creature can be loaded yet invisible: its last LOD normally culls the mesh once it looks small enough on screen.
    // Keep that last mesh until the zone search unloads the creature, retaining any earlier switches in model detail.
    [HarmonyPatch]
    internal static class DistantCreatureMeshes
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(BaseAI), "Awake");
            yield return AccessTools.Method(typeof(Leviathan), "Awake");
        }

        private static void Postfix(Component __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsDedicated()) LastLod.Keep(__instance);
        }
    }
}
