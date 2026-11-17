using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Inventory), "FindEmptySlot")]
    internal static class InventoryPlacement
    {
        private static void Prefix(ref bool topFirst)
        {
            topFirst = true;
        }
    }
}
