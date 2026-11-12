using System.Collections.Generic;
using System.Reflection;
using EpicLoot;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot's set marker also lit up for the game's own armor sets, such as Fenris. Only its own sets keep the marker.
    // Tooltips and set bonuses make their own IsSetItem calls, so they still see both kinds of set.
    [HarmonyPatch]
    internal static class EpicLootSetMarkers
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(API), nameof(API.ApplyMagicItemBackground));
            yield return AccessTools.Method(typeof(ItemBackgroundHelper), nameof(ItemBackgroundHelper.UpdateIconSetMarker));
        }

        private static List<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(ItemDataExtensions), nameof(ItemDataExtensions.IsSetItem))),
                AccessTools.Method(typeof(ItemDataExtensions), nameof(ItemDataExtensions.IsMagicSetItem)), 1, "EpicLoot's set marker check changed.");
    }
}
