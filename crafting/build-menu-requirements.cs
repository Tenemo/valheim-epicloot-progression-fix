using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Valheim colors hammer buttons only during Setup. Moving across station or chest range can change
    // building requirements while the open menu retains those colors. Refresh the existing buttons.
    [HarmonyPatch(typeof(BuildUi), "Update")]
    internal static class BuildMenuRequirements
    {
        private static float nextRefresh;
        // The frame in which the game last set the buttons up, until a later frame starts the wait.
        private static int setUpFrame = -1;
        // The isolated test client sets false to compare recoloring the buttons the game has just set up.
        internal static bool WaitAfterSetup = true;

        private static void Postfix(List<BuildUiPieceButton> ___m_pieceButtons)
        {
            if (setUpFrame >= 0)
            {
                if (Time.frameCount == setUpFrame) return;
                setUpFrame = -1;
                nextRefresh = Time.unscaledTime + 0.2f;
                return;
            }
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.2f;
            foreach (var button in ___m_pieceButtons)
                if (button.gameObject.activeInHierarchy) button.UpdateRequirements();
        }

        // Setting the buttons up, as the menu opens, changes tab or searches, colors them, so the next refresh waits its 0.2 s.
        // The wait starts with the following frame: the frame that sets the buttons up can itself last longer than 0.2 s.
        [HarmonyPatch(typeof(BuildUi), "UpdatePieceButtons")]
        private static class SetUp
        {
            private static void Postfix() { if (WaitAfterSetup) setUpFrame = Time.frameCount; }
        }
    }
}
