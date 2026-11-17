using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Build tools clear the interaction hover. The creature HUD can still use the normal raycast without
    // exposing interaction targets or changing the hammer's placement and repair controls.
    [HarmonyPatch(typeof(Player), nameof(Player.GetHoverCreature))]
    internal static class BuildToolHealthBars
    {
        private delegate void HoverSearch(Player player, out GameObject hoveredObject, out Character hoveredCreature);
        private static readonly HoverSearch FindHoverObject = AccessTools.MethodDelegate<HoverSearch>(
            AccessTools.Method(typeof(Player), "FindHoverObject"));

        private static void Postfix(Player __instance, ref Character __result)
        {
            if (__result || !__instance.InPlaceMode() || __instance.IsDead() || __instance.GetDoodadController() != null)
                return;
            FindHoverObject(__instance, out _, out __result);
        }
    }
}
