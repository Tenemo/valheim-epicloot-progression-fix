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
        // Several HUD parts ask every frame, and each search is a long raycast; the view changes only between frames.
        private static int searchedFrame = -1;
        private static Player searcher;
        private static Character found;

        private static void Postfix(Player __instance, ref Character __result)
        {
            if (__result || !__instance.InPlaceMode() || __instance.IsDead() || __instance.GetDoodadController() != null)
                return;
            if (searchedFrame != Time.frameCount || searcher != __instance)
            {
                FindHoverObject(__instance, out _, out found);
                searchedFrame = Time.frameCount; searcher = __instance;
            }
            __result = found;
        }
    }
}
