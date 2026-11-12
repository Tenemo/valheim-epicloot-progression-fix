using EpicLoot.Crafting;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    // EpicLoot sizes Augment's choice labels up to 21 points, so a short effect such as Weightless stands far above longer
    // ones that shrink to fit their button. The labels keep the 16 points of the game's other buttons and still shrink to fit.
    [HarmonyPatch(typeof(AugmentHelper), "ConvertLabelToTmp")]
    internal static class AugmentChoiceLabels
    {
        private static void Postfix(Button button)
        {
            var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
            label.fontSizeMax = 16f;
            label.fontSize = 16f;
        }
    }
}
