using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // MultiUserChest admits a request for any bag cell by the cells that are empty when it is made, and drops whatever
    // no longer fits when the reply arrives. Take All makes such requests together, so several could count on the same
    // last cells and the rest fell to the ground, at sea into the water. Such a request now takes a free cell of its own,
    // in native placement order, or leaves the item in the chest. Native placement remains for a lone transfer that
    // existing stacks absorb or that has no free cell to take.
    internal static class ChestRequestRoom
    {
        private static IDictionary pending;
        private static Type request;
        private static PropertyInfo source, destination;
        private static ConstructorInfo refusal;
        private static MethodInfo blockGet, slotBlocked;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.MultiUserChest, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            pending = AccessTools.Field(assembly.GetType("MultiUserChest.PackageHandler", true), "Packages")?.GetValue(null) as IDictionary;
            request = assembly.GetType("MultiUserChest.IRequest", true);
            source = AccessTools.Property(request, "SourceInventory"); destination = AccessTools.Property(request, "TargetInventory");
            refusal = AccessTools.Constructor(assembly.GetType("MultiUserChest.RequestChestRemove", true),
                new[] { typeof(Vector2i), typeof(Vector2i), typeof(int), typeof(ItemDrop.ItemData), typeof(Inventory), typeof(Inventory) });
            var block = assembly.GetType("MultiUserChest.InventoryBlock", true);
            blockGet = AccessTools.Method(block, "Get"); slotBlocked = AccessTools.Method(block, "IsSlotBlocked");
            var remove = AccessTools.Method(assembly.GetType("MultiUserChest.ContainerHandler", true), "RemoveItemFromChest");
            if (pending == null || source == null || destination == null || refusal == null || blockGet == null || slotBlocked == null || remove == null)
                throw new InvalidOperationException("MultiUserChest's chest removal changed.");
            harmony.Patch(remove, prefix: new HarmonyMethod(typeof(ChestRequestRoom), nameof(Admit)));
        }

        private static bool Admit(Container container, ItemDrop.ItemData item, Inventory destinationInventory, ref Vector2i to, int dragAmount, ref object __result)
        {
            if (to.x >= 0 && to.y >= 0 || item == null || destinationInventory == null) return true;
            int amount = dragAmount >= 0 ? Mathf.Min(dragAmount, item.m_stack) : item.m_stack;
            bool cell = FreeCell(destinationInventory, out var free);
            if (!Waiting(destinationInventory) && (!cell || destinationInventory.FindFreeStackSpace(item.m_shared.m_name, item.m_worldLevel) >= amount)) return true;
            if (cell) { to = free; return true; }
            __result = refusal.Invoke(new object[] { Vector2i.zero, Vector2i.zero, 0, null, container.GetInventory(), destinationInventory });
            return false;
        }

        // Any unanswered transfer into or out of the bag.
        private static bool Waiting(Inventory inventory)
        {
            foreach (var package in pending.Values)
                if (request.IsInstanceOfType(package) && (destination.GetValue(package) == inventory || source.GetValue(package) == inventory)) return true;
            return false;
        }

        // Empty, not kept for a returning recall weapon and not waiting for another transfer, in the rows players see.
        private static bool FreeCell(Inventory inventory, out Vector2i cell)
        {
            var block = blockGet.Invoke(null, new object[] { inventory });
            int rows = EquipmentAndQuickSlots.VisibleRows?.Invoke() ?? inventory.GetHeight();
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < inventory.GetWidth(); x++)
                {
                    cell = new Vector2i(x, y);
                    if (inventory.GetItemAt(x, y) == null && !RecallRecovery.Reserves(inventory, cell) && !(bool)slotBlocked.Invoke(block, new object[] { cell })) return true;
                }
            cell = default; return false;
        }
    }
}
