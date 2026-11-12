using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // A thrown recalling weapon keeps its inventory cell until it returns: automatic placement, capacity checks and moves
    // to a chosen cell treat the kept cell as taken, while merges into existing stacks still happen. EquipmentAndQuickSlots
    // keeps deciding which cells may take an item. The weapon then takes its cell back through native insertion.
    internal static class RecallSlots
    {
        private static readonly ItemDrop.ItemData Kept = new ItemDrop.ItemData();

        // Rows whose empty cells the native and EquipmentAndQuickSlots counts include; its quick slots ask QuickSlots below.
        private static int CountedRows(Inventory inventory) => EquipmentAndQuickSlots.VisibleRows?.Invoke() ?? inventory.GetHeight();

        private static ItemDrop.ItemData Occupant(Inventory inventory, int x, int y) =>
            inventory.GetItemAt(x, y) ?? (RecallRecovery.Reserves(inventory, new Vector2i(x, y)) ? Kept : null);

        [HarmonyPatch(typeof(Inventory), "FindEmptySlot")]
        private static class AutomaticPlacement
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(Inventory), nameof(Inventory.GetItemAt))),
                    AccessTools.Method(typeof(RecallSlots), nameof(Occupant)), 2, "Inventory.FindEmptySlot changed; recalling weapons cannot keep their cells.");
        }

        [HarmonyPatch(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) })]
        private static class ChosenCell
        {
            // Drag and drop, Take All and other moves to a chosen cell. Loading a saved inventory skips this position check.
            private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, int x, int y, bool skipValidPositionCheck, ref bool __result)
            {
                if (skipValidPositionCheck || !RecallRecovery.Reserves(__instance, new Vector2i(x, y), item)) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetEmptySlots))]
        private static class EmptyCells
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Inventory __instance, ref int __result) =>
                __result -= RecallRecovery.ReservedFreeCells(__instance, CountedRows(__instance));
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveEmptySlot))]
        private static class AnyEmptyCell
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Inventory __instance, ref bool __result)
            {
                if (__result && RecallRecovery.ReservedFreeCells(__instance, CountedRows(__instance)) > 0) __result = __instance.GetEmptySlots() > 0;
            }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.CanAddItem), new[] { typeof(ItemDrop.ItemData), typeof(int) })]
        private static class Capacity
        {
            // Runs after EquipmentAndQuickSlots has restored the rows and slot items it hides during the native count.
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Inventory __instance, ItemDrop.ItemData item, int stack, ref bool __result)
            {
                if (RecallRecovery.PreferredCell(__instance, item, out _)) __result = true;
                else if (__result && RecallRecovery.ReservedFreeCells(__instance, CountedRows(__instance)) > 0)
                    __result = __instance.FindFreeStackSpace(item.m_shared.m_name, item.m_worldLevel) +
                        __instance.GetEmptySlots() * item.m_shared.m_maxStackSize >= (stack > 0 ? stack : item.m_stack);
            }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData) })]
        private static class Return
        {
            // Pickup and saved recovery both insert here; only the returning weapon may take its own kept cell.
            private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
            {
                if (!RecallRecovery.PreferredCell(__instance, item, out var cell) || !__instance.AddItem(item, cell)) return true;
                __result = true;
                return false;
            }

            private static void Postfix(Inventory __instance, ItemDrop.ItemData item, bool __result)
            {
                if (__result) RecallRecovery.Returned(__instance, item);
            }
        }

        // EquipmentAndQuickSlots fills its quick slots, including when the visible rows are full, through this check.
        [HarmonyPatch]
        private static class QuickSlots
        {
            private static bool Prepare() => PluginIds.TryGet(PluginIds.EquipmentAndQuickSlots, out _);

            private static MethodBase TargetMethod() => PluginIds.TryGet(PluginIds.EquipmentAndQuickSlots, out var plugin)
                ? AccessTools.PropertyGetter(plugin.Instance.GetType().Assembly.GetType("EquipmentAndQuickSlots.Slots+Slot", true), "IsFree")
                : null;

            private static void Postfix(Vector2i ____gridPos, ref bool __result)
            {
                if (__result && RecallRecovery.Reserves(Player.m_localPlayer?.GetInventory(), ____gridPos)) __result = false;
            }
        }
    }
}
