using System;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EquipmentAndQuickSlots 3.1.3 keeps worn armor, the trinket and the quick slot items in three hidden rows below the
    // visible rows of the player's inventory. Quick Stack 1.4.15 no longer checks for EquipmentAndQuickSlots, so its sort,
    // Store All and Quick Stack took those rows for ordinary cells: sorting moved worn gear into the grid, still worn, and
    // other items into the slots, and Store All and Quick Stack emptied the quick slots into chests. Count every hidden
    // cell as an equipment or quick slot, as Quick Stack does for other slot mods; restocking may still fill quick slots.
    internal static class QuickStackEquipmentSlots
    {
        private delegate bool SlotCell(int x, int y, ref string slotId);
        private static SlotCell slotCell;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.QuickStack, out var quickStack) ||
                !PluginIds.TryGet(PluginIds.EquipmentAndQuickSlots, out var slots)) return;
            var check = AccessTools.Method(quickStack.Instance.GetType().Assembly.GetType("QuickStackStore.CompatibilitySupport", true),
                "InternalIsEquipOrQuickSlot");
            if (check == null) throw new InvalidOperationException("Quick Stack's slot check moved.");
            var api = slots.Instance.GetType().Assembly.GetType("EquipmentAndQuickSlots.API");
            var cell = api == null ? null : AccessTools.Method(api, "IsSlotCell", new[] { typeof(int), typeof(int), typeof(string).MakeByRefType() });
            if (cell == null) throw new InvalidOperationException("EquipmentAndQuickSlots' slot API changed.");
            slotCell = AccessTools.MethodDelegate<SlotCell>(cell);
            harmony.Patch(check, postfix: new HarmonyMethod(typeof(QuickStackEquipmentSlots), nameof(HiddenRows)));
            log.LogInfo("Quick Stack's sort, Store All and Quick Stack leave EquipmentAndQuickSlots' equipment and quick slots alone.");
        }

        // Quick Stack asks only about the player's own inventory, and without includeRestockableSlots only to restock.
        private static void HiddenRows(Vector2i itemPos, bool includeRestockableSlots, ref bool __result)
        {
            if (__result || itemPos.y < EquipmentAndQuickSlots.VisibleRows()) return;
            string slot = null;
            bool quickSlot = slotCell(itemPos.x, itemPos.y, ref slot) && slot.StartsWith("Quick", StringComparison.Ordinal);
            __result = includeRestockableSlots || !quickSlot;
        }
    }
}
