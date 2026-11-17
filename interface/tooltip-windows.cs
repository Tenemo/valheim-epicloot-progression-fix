using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The game keeps one tooltip window and fills it for whichever control the pointer reaches next, though controls use
    // different window templates. EpicLoot keeps a window past the pointer leaving its control, so a skill row reached from
    // the Skills button showed its framed tooltip in the button's plain window, without background or title. A control
    // whose template differs from the open window's now gets a fresh window from its own; the same template keeps the
    // open window, its contents and its scroll position.
    [HarmonyPatch]
    internal static class TooltipWindows
    {
        private static readonly AccessTools.FieldRef<GameObject> Open = AccessTools.StaticFieldRefAccess<GameObject>(AccessTools.Field(typeof(UITooltip), "m_tooltip"));
        // The open window and the template it was made from.
        private static GameObject window, template;

        // Closes the open window when it was made from another template than the control's.
        internal static void Replace(UITooltip tooltip)
        {
            var open = Open();
            if (open && open == window && template != tooltip.m_tooltipPrefab) UITooltip.HideTooltip();
        }

        [HarmonyPatch(typeof(UITooltip), nameof(UITooltip.OnHoverStart)), HarmonyPrefix]
        private static void Opening(UITooltip __instance) => Replace(__instance);

        [HarmonyPatch(typeof(UITooltip), nameof(UITooltip.OnHoverStart)), HarmonyPostfix]
        private static void Opened(UITooltip __instance)
        {
            var open = Open();
            if (open != window) { window = open; template = __instance.m_tooltipPrefab; }
        }
    }
}
