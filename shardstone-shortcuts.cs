using System;
using System.Collections.Generic;
using System.Linq;
using EpicLoot;
using EpicLoot.ShardStones;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class ShardstoneShortcuts
    {
        private static readonly Action<Inventory, bool, bool> NotifyInventory =
            AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(AccessTools.Method(typeof(Inventory), "Changed"));
        private static readonly Action<string> ShowMessage = AccessTools.MethodDelegate<Action<string>>(
            AccessTools.Method(typeof(ShardUI), "ShowSocketMessage"));
        private static readonly AccessTools.FieldRef<InventoryGui, EffectList> MoveEffects =
            AccessTools.FieldRefAccess<InventoryGui, EffectList>("m_moveItemEffects");

        internal static bool Open(InventoryGui gui) => gui != null && InventoryGui.IsVisible() &&
            OpenSockets.Row != null && OpenSockets.Item != null && Player.m_localPlayer != null &&
            gui.m_playerGrid.GetInventory() == Player.m_localPlayer.GetInventory() &&
            Player.m_localPlayer.GetInventory().ContainsItem(OpenSockets.Item);

        private static bool SameStone(ItemDrop.ItemData left, ItemDrop.ItemData right) =>
            left != null && right != null && Shards.IsShard(left) && Shards.IsShard(right) &&
            ShardSocketManager.GetSourcePrefabName(left) == ShardSocketManager.GetSourcePrefabName(right);

        // The same stone, or a rune with the same effect, rarity, extraction history and set identity.
        // Favorites and other unrelated item data do not count.
        internal static bool Equivalent(ItemDrop.ItemData left, ItemDrop.ItemData right) => SameStone(left, right) ||
            RuneStacks.IsRune(left) && RuneStacks.IsRune(right) && left.m_shared.m_name == right.m_shared.m_name &&
            left.m_quality == right.m_quality && RuneStacks.SameMagic(left, right);

        internal static bool Socketable(ItemDrop.ItemData item) => item != null && (Shards.IsShard(item) || RuneStacks.IsRune(item));

        internal static void Refuse(string reason) => ShowMessage(reason);

        // Whether the open item's socket takes the stone or rune, showing the reason when it doesn't. An equivalent occupant
        // counts as a fit with nothing to do.
        private static bool Fits(ItemDrop.ItemData input, int slot, out bool same)
        {
            var sockets = OpenSockets.Row;
            var occupant = sockets.GetItemAt(slot, 0);
            same = Equivalent(input, occupant);
            if (same) return true;
            string reason;
            if (occupant == null)
            {
                if (!ShardSocketManager.CanSocket(OpenSockets.Item, input, out reason))
                { ShowMessage(reason); return false; }
                return true;
            }
            var removal = ShardSocketManager.GetRemovalPolicy(OpenSockets.Item, occupant);
            if (removal != SocketRemoval.Free)
            { ShowMessage(ShardSocketManager.DescribeRemovalPolicy(removal)); return false; }
            if (!ShardSocketManager.CanCoexist(OpenSockets.Item, input,
                sockets.GetAllItems().Where(item => item != occupant), out reason))
            { ShowMessage(reason); return false; }
            return true;
        }

        // A stone or rune pointed at in an open chest is checked first, so a refused one never leaves the chest, then one of
        // it is withdrawn into the bag and placed once the chest delivers it.
        internal static bool PlaceFromChest(InventoryGui gui, ItemDrop.ItemData input, int slot)
        {
            if (!Open(gui) || !Socketable(input) || input.m_stack < 1 || slot < 0 || slot >= OpenSockets.Row.GetWidth() ||
                ChestHotbarTransfer.Waiting) return false;
            if (!Fits(input, slot, out bool same)) return false;
            return same || ChestHotbarTransfer.Socket(gui, input, slot);
        }

        internal static bool Place(InventoryGui gui, ItemDrop.ItemData input, int slot, bool playEffect = true)
        {
            if (!Open(gui) || !Socketable(input)) return false;
            var sockets = OpenSockets.Row;
            var source = Player.m_localPlayer.GetInventory();
            if (!source.ContainsItem(input) || input.m_stack < 1 || slot < 0 || slot >= sockets.GetWidth()) return false;
            if (!Fits(input, slot, out bool same)) return false;
            if (same) return true;
            var occupant = sockets.GetItemAt(slot, 0);
            if (occupant != null && input.m_stack > 1 && !source.CanAddItem(occupant, 1))
            { ShowMessage("$inventory_full"); return false; }

            var incoming = input.Clone();
            incoming.m_stack = 1;
            incoming.m_gridPos = new Vector2i(slot, 0);
            var sourceItems = source.GetAllItems();
            var socketItems = sockets.GetAllItems();
            int inputIndex = sourceItems.IndexOf(input), socketIndex = socketItems.IndexOf(occupant);
            Vector2i oldPosition = occupant?.m_gridPos ?? default;
            var changed = GameFields.InventoryChanged(source);
            // Publish one complete exchange. EpicLoot saves the sockets only after both inventories are final.
            GameFields.InventoryChanged(source) = null;
            try
            {
                if (--input.m_stack == 0) sourceItems.Remove(input);
                if (occupant != null)
                {
                    socketItems.Remove(occupant);
                    bool returned = source.AddItem(occupant);
                    if (input.m_stack == 0 && (!returned || sourceItems.Contains(occupant)))
                    {
                        // Merge first; a separate returned item keeps the cell freed by the last incoming stone.
                        occupant.m_gridPos = input.m_gridPos;
                        if (!returned) sourceItems.Insert(inputIndex, occupant);
                        returned = true;
                    }
                    if (!returned)
                    {
                        input.m_stack++;
                        occupant.m_gridPos = oldPosition;
                        socketItems.Insert(socketIndex, occupant);
                        NotifyInventory(source, false, false);
                        ShowMessage("$inventory_full");
                        return false;
                    }
                }
                socketItems.Add(incoming);
            }
            finally { GameFields.InventoryChanged(source) = changed; }
            NotifyInventory(sockets, false, false);
            NotifyInventory(source, true, false);
            if (playEffect) MoveEffects(gui).Create(gui.transform.position, Quaternion.identity);
            return true;
        }

        // A socketed rune or stone moves to another socket of the open row through EpicLoot's own socket-to-socket drop,
        // which swaps it with an occupant, keeps both items as they are and saves the row once.
        internal static bool Move(InventoryGui gui, ItemDrop.ItemData socketed, int slot)
        {
            var sockets = OpenSockets.Row;
            var grid = OpenSockets.Grid;
            if (!Open(gui) || grid == null || grid.GetInventory() != sockets || !sockets.ContainsItem(socketed) ||
                slot < 0 || slot >= sockets.GetWidth() || socketed.m_gridPos.x == slot) return false;
            if (!grid.DropItem(sockets, socketed, socketed.m_stack, new Vector2i(slot, 0))) return false;
            MoveEffects(gui).Create(gui.transform.position, Quaternion.identity);
            return true;
        }

        // Ctrl-click fills an empty socket through EpicLoot's native insertion. Only a full row swaps, for a shardstone
        // or an etched rune alike, the first socketed stone or rune that differs from it, with the usual removal,
        // compatibility and bag-space checks.
        internal static bool QuickSwap(InventoryGui gui, ItemDrop.ItemData input)
        {
            if (!Open(gui) || !Socketable(input)) return false;
            var sockets = OpenSockets.Row;
            if (Enumerable.Range(0, sockets.GetWidth()).Any(slot => sockets.GetItemAt(slot, 0) == null)) return false;
            for (int slot = 0; slot < sockets.GetWidth(); slot++)
            {
                var occupant = sockets.GetItemAt(slot, 0);
                if (occupant != null && Socketable(occupant) && !Equivalent(input, occupant))
                {
                    Place(gui, input, slot);
                    return true;
                }
            }
            return false;
        }

    }

    // One socket action per click. MouseTweaks repeats Ctrl transfers every frame while the mouse button is held, and
    // a stone returned under the cursor was swapped straight back, so a held click flashed between two stones.
    internal static class SocketClick
    {
        private static int lastFrame = -1;

        internal static bool Fresh()
        {
            // MouseTweaks reads the button through Unity's input, Valheim through ZInput.
            bool held = Input.GetKey(KeyCode.Mouse0) || ZInput.GetMouseButton(0);
            bool pressed = Input.GetKeyDown(KeyCode.Mouse0) || ZInput.GetMouseButtonDown(0);
            if (held && !pressed) return false;
            if (lastFrame == Time.frameCount) return false;
            lastFrame = Time.frameCount;
            return true;
        }
    }

    [HarmonyPatch(typeof(ShardUI), "QuickSocket")]
    internal static class ShardstoneQuickSwap
    {
        private static bool Prefix(InventoryGui invGui, ItemDrop.ItemData item) => SocketClick.Fresh() &&
            (!(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) || !ShardstoneShortcuts.QuickSwap(invGui, item));
    }

    [HarmonyPatch(typeof(ShardUI), "QuickRemoveFromSocket")]
    internal static class ShardstoneQuickRemoval
    {
        private static bool Prefix() => SocketClick.Fresh();
    }

    [HarmonyPatch(typeof(ShardUI), "SwapIntoSocket")]
    internal static class ShardstoneDragSwap
    {
        private static bool Prefix(Inventory source, ItemDrop.ItemData input, Vector2i pos,
            ItemDrop.ItemData occupant, ref bool __result)
        {
            var gui = InventoryGui.instance;
            if (!ShardstoneShortcuts.Open(gui) || source != Player.m_localPlayer.GetInventory() ||
                !Shards.IsShard(input) || pos.y != 0 || OpenSockets.Row.GetItemAt(pos.x, pos.y) != occupant)
                return true;
            __result = ShardstoneShortcuts.Place(gui, input, pos.x, playEffect: false);
            return false;
        }
    }
}
