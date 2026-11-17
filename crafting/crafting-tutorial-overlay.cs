using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    // AAA_Crafting 2.1.12's tutorial darkens the screen except the part of the crafting window it explains. It starts the
    // first time a player opens the inventory and from the ? button in the crafting window. Esc ended it and, in the same
    // frame, drew the darkening again around the window, which still counts as open for a frame after Esc closes it. With
    // the tutorial over, nothing hid the darkening, so the screen stayed dark outside where the window had been until the
    // game restarted. The darkening now hides whenever the tutorial is not running.
    internal static class CraftingTutorialOverlay
    {
        private static AccessTools.FieldRef<object, bool> running;
        private static AccessTools.FieldRef<object, Image> darkening;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.AaaCrafting, out var plugin)) return;
            var tutorial = plugin.Instance.GetType().Assembly.GetType("TutorialOverlay");
            var update = tutorial == null ? null : AccessTools.Method(tutorial, "Update", Type.EmptyTypes);
            if (update == null)
                throw new InvalidOperationException("AAA_Crafting's tutorial moved.");
            running = AccessTools.FieldRefAccess<bool>(tutorial, "tutorialActive");
            darkening = AccessTools.FieldRefAccess<Image>(tutorial, "overlayImage");
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(CraftingTutorialOverlay), nameof(HideWhenEnded)));
            log.LogInfo("AAA_Crafting's tutorial darkening hides when the tutorial ends.");
        }

        private static void HideWhenEnded(object __instance)
        {
            if (running(__instance)) return;
            var image = darkening(__instance);
            if (image != null && image.enabled) image.enabled = false;
        }
    }
}
