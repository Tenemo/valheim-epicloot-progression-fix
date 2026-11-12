using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A hotbar number key over an item in an open chest takes the whole stack into that hotbar slot through
    // MultiUserChest, as dragging it there does. An item already in the slot first moves to the first free bag cell.
    // Right-clicking equipment in an open chest withdraws it into the first free bag cell the same way, then equips it
    // once the chest owner confirms the delivery, so anything it replaces stays in the player's inventory. A number key
    // over a stone or rune in the chest with sockets open withdraws one of it the same way, then sockets it.
    internal static class ChestHotbarTransfer
    {
        private sealed class Withdrawal
        {
            internal Vector2i Cell;
            internal string Prefab;
            internal int Quality, Variant;
            internal float Deadline;
            // For a socket withdrawal: the stone or rune as the chest held it, the item whose sockets were open and the socket.
            internal ItemDrop.ItemData Stone, Host;
            internal int Socket = -1;
        }

        private static Withdrawal pending;

        // An earlier withdrawal still waits for the chest's reply.
        internal static bool Waiting => pending != null && Time.time < pending.Deadline;
        private static readonly AccessTools.FieldRef<InventoryGui, Container> OpenContainer = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
        private static readonly Action<Inventory, bool, bool> Changed = AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(AccessTools.Method(typeof(Inventory), "Changed"));
        private static MethodInfo removeFromChest, blockGet, slotBlocked;

        internal static void Initialize(IntegrationPatches patches)
        {
            if (!PluginIds.TryGet(PluginIds.MultiUserChest, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            removeFromChest = AccessTools.Method(assembly.GetType("MultiUserChest.ContainerHandler", true), "RemoveItemFromChest");
            var block = assembly.GetType("MultiUserChest.InventoryBlock", true);
            blockGet = AccessTools.Method(block, "Get"); slotBlocked = AccessTools.Method(block, "IsSlotBlocked");
            if (removeFromChest == null || blockGet == null || slotBlocked == null) throw new InvalidOperationException("MultiUserChest's chest removal moved.");
            var self = typeof(ChestHotbarTransfer);
            // After EpicLoot's socket controls, before MultiUserChest's shared-chest consumption.
            patches.Patch(AccessTools.Method(typeof(InventoryGui), "OnRightClickItem"),
                prefix: new HarmonyMethod(self, nameof(EquipFromChest)) { after = new[] { PluginIds.EpicLoot }, before = new[] { PluginIds.MultiUserChest } });
            patches.Patch(AccessTools.Method(assembly.GetType("MultiUserChest.InventoryHandler", true), "RPC_RequestItemRemoveResponse",
                new[] { typeof(Inventory), assembly.GetType("MultiUserChest.RequestChestRemoveResponse", true) }), postfix: new HarmonyMethod(self, nameof(Delivered)));
        }

        // Empty, not kept for a returning recall weapon, and not waiting for another chest transfer.
        private static bool Free(Inventory inventory, Vector2i cell) => inventory.GetItemAt(cell.x, cell.y) == null && !RecallRecovery.Reserves(inventory, cell) &&
            !(bool)slotBlocked.Invoke(blockGet.Invoke(null, new object[] { inventory }), new object[] { cell });

        private static bool BagCell(Inventory inventory, out Vector2i cell)
        {
            int rows = EquipmentAndQuickSlots.VisibleRows?.Invoke() ?? inventory.GetHeight();
            for (int y = 1; y < rows; y++)
                for (int x = 0; x < inventory.GetWidth(); x++)
                    if (Free(inventory, new Vector2i(x, y))) { cell = new Vector2i(x, y); return true; }
            cell = default; return false;
        }

        internal static void Start(InventoryGui gui, ItemDrop.ItemData item, int slot)
        {
            var player = Player.m_localPlayer; var container = OpenContainer(gui);
            if (removeFromChest == null || !container || container.GetComponent<TombStone>() ||
                !gui.IsContainerOpen() || !GameFields.ContainerGrid(gui).gameObject.activeInHierarchy || GameFields.ContainerGrid(gui).GetInventory() != container.GetInventory()) return;
            var view = GameFields.ContainerView(container);
            if (!view || !view.IsValid() || !view.HasOwner() || !container.GetInventory().ContainsItem(item)) return;
            var inventory = player.GetInventory(); var hotbar = new Vector2i(slot, 0);
            var occupant = inventory.GetItemAt(slot, 0);
            if (occupant == null && !Free(inventory, hotbar) || RecallRecovery.Reserves(inventory, hotbar)) return;
            if (occupant != null)
            {
                if (!BagCell(inventory, out var cell)) { player.Message(MessageHud.MessageType.Center, "$msg_noroom"); return; }
                occupant.m_gridPos = cell; Changed(inventory, false, false);
            }
            removeFromChest.Invoke(null, new object[] { container, item, inventory, hotbar, player.GetZDOID(), item.m_stack, null });
        }

        private static bool EquipFromChest(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, bool __runOriginal)
        {
            var player = Player.m_localPlayer; var container = OpenContainer(__instance);
            if (!__runOriginal || item == null || !player || !item.IsEquipable() || !container || container.GetComponent<TombStone>() ||
                grid.GetInventory() != container.GetInventory() || grid.GetInventory() == player.GetInventory()) return true;
            if (Waiting) return false;
            pending = null;
            var view = GameFields.ContainerView(container);
            if (!view || !view.IsValid() || !view.HasOwner() || !container.GetInventory().ContainsItem(item)) return false;
            var inventory = player.GetInventory();
            if (!BagCell(inventory, out var cell)) { player.Message(MessageHud.MessageType.Center, "$inventory_full"); return false; }
            Withdraw(container, item, item.m_stack, new Withdrawal { Cell = cell });
            return false;
        }

        // One stone or rune from the open chest into the first free bag cell, socketed on delivery while the same item's
        // sockets are still open.
        internal static bool Socket(InventoryGui gui, ItemDrop.ItemData item, int slot)
        {
            var player = Player.m_localPlayer; var container = OpenContainer(gui);
            if (removeFromChest == null || !player || Waiting || !container || container.GetComponent<TombStone>() || !gui.IsContainerOpen() ||
                GameFields.ContainerGrid(gui).GetInventory() != container.GetInventory()) return false;
            pending = null;
            var view = GameFields.ContainerView(container);
            if (!view || !view.IsValid() || !view.HasOwner() || !container.GetInventory().ContainsItem(item)) return false;
            if (!BagCell(player.GetInventory(), out var cell)) { ShardstoneShortcuts.Refuse("$inventory_full"); return false; }
            Withdraw(container, item, 1, new Withdrawal { Cell = cell, Stone = item.Clone(), Host = OpenSockets.Item, Socket = slot });
            return true;
        }

        private static void Withdraw(Container container, ItemDrop.ItemData item, int amount, Withdrawal withdrawal)
        {
            var player = Player.m_localPlayer; var inventory = player.GetInventory();
            withdrawal.Prefab = item.m_dropPrefab ? item.m_dropPrefab.name : null; withdrawal.Quality = item.m_quality;
            withdrawal.Variant = item.m_variant; withdrawal.Deadline = Time.time + 10f;
            pending = withdrawal;
            removeFromChest.Invoke(null, new object[] { container, item, inventory, withdrawal.Cell, player.GetZDOID(), amount, null });
            // MultiUserChest refused before sending anything, so no reply will come.
            if (pending == withdrawal && !(bool)slotBlocked.Invoke(blockGet.Invoke(null, new object[] { inventory }), new object[] { withdrawal.Cell })) pending = null;
        }

        // A reply releases the reserved cell; a successful one has put the chest item there. An uncertain withdrawal
        // is never retried, and the delivered item stays in the bag when it can no longer be equipped or socketed.
        private static void Delivered(Inventory __0)
        {
            var player = Player.m_localPlayer;
            if (pending == null || !player || __0 != player.GetInventory() ||
                (bool)slotBlocked.Invoke(blockGet.Invoke(null, new object[] { __0 }), new object[] { pending.Cell })) return;
            var expected = pending; pending = null;
            var item = __0.GetItemAt(expected.Cell.x, expected.Cell.y);
            if (item == null || !item.m_dropPrefab || item.m_dropPrefab.name != expected.Prefab || item.m_quality != expected.Quality ||
                item.m_variant != expected.Variant) return;
            if (expected.Socket >= 0)
            {
                if (OpenSockets.Item == expected.Host && ShardstoneShortcuts.Equivalent(item, expected.Stone))
                    ShardstoneShortcuts.Place(InventoryGui.instance, item, expected.Socket);
                return;
            }
            if (!player.IsItemEquiped(item)) player.UseItem(__0, item, true);
        }
    }
}
