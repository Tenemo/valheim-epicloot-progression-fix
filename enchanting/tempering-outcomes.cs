using EpicLoot;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(TemperPanel), "CanTemper")]
    internal static class TemperingChanceButton
    {
        private static readonly AccessTools.FieldRef<TemperPanel, TemperData> Data = AccessTools.FieldRefAccess<TemperPanel, TemperData>("temperData");
        private static void Postfix(TemperPanel __instance, ref bool __result) => __result &= Data(__instance)?.probability > 0f;
    }

}
