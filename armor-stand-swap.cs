using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Left Alt + E at an armor stand exchanges the six worn wearables, head, chest, legs, shoulders, utility and trinket,
    // with what the stand holds in those positions, all at once: a piece on both sides swaps, a worn piece facing an empty
    // position hangs up and a stored piece facing an empty worn slot is put on. Weapons and shields on the stand and
    // everything else carried stay as they are. Once this game owns the stand the exchange happens in one update without
    // the equip queue or its delays; it refuses before moving anything when a piece cannot hang up, cannot be worn or has no
    // room, and undoes itself if the game still refuses a piece. A stored piece goes where the worn one it replaces was, or
    // to its EquipmentAndQuickSlots slot.
    internal static class ArmorStandSwap
    {
        private static readonly ItemDrop.ItemData.ItemType[] Wearables =
        {
            ItemDrop.ItemData.ItemType.Helmet, ItemDrop.ItemData.ItemType.Chest, ItemDrop.ItemData.ItemType.Legs,
            ItemDrop.ItemData.ItemType.Shoulder, ItemDrop.ItemData.ItemType.Utility, ItemDrop.ItemData.ItemType.Trinket,
        };
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData>[] Worn =
            new[] { "m_helmetItem", "m_chestItem", "m_legItem", "m_shoulderItem", "m_utilityItem", "m_trinketItem" }
                .Select(name => AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>(name)).ToArray();
        private static readonly AccessTools.FieldRef<Player, List<Player.MinorActionData>> ActionQueue =
            AccessTools.FieldRefAccess<Player, List<Player.MinorActionData>>("m_actionQueue");
        private static readonly AccessTools.FieldRef<Inventory, List<ItemDrop.ItemData>> Items =
            AccessTools.FieldRefAccess<Inventory, List<ItemDrop.ItemData>>("m_inventory");
        private static readonly Action<Inventory, bool, bool> Changed =
            AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(AccessTools.Method(typeof(Inventory), "Changed", new[] { typeof(bool), typeof(bool) }));

        // One of the six positions the exchange changes. Native slots and extra positions are -1 when unused.
        private sealed class Position
        {
            internal ItemDrop.ItemData Worn, Stored;
            internal int From = -1, FromExtra = -1, To = -1, ToExtra = -1;
            internal Vector2i WornCell, Cell;
        }

        [HarmonyPatch(typeof(Switch), nameof(Switch.Interact))]
        private static class Shortcut
        {
            private static bool Prefix(Switch __instance, Humanoid character, bool hold, ref bool __result)
            {
                if (!ZInput.GetKey(KeyCode.LeftAlt) || character != Player.m_localPlayer) return true;
                var stand = ArmorStandStorage.Stand(__instance);
                if (stand == null) return true;
                __result = !hold && PrivateArea.CheckAccess(stand.transform.position, 0f, true);
                var player = (Player)character;
                if (__result) ArmorStandStorage.WhenOwner(stand, player, view => Exchange(stand, view, player));
                return false;
            }
        }

        [HarmonyPatch(typeof(Switch), nameof(Switch.GetHoverText))]
        private static class Hover
        {
            private static void Postfix(Switch __instance, ref string __result)
            {
                var stand = ArmorStandStorage.Stand(__instance);
                var view = stand ? ArmorStandStorage.View(stand) : null;
                if (view == null || !view.IsValid() || !PrivateArea.CheckAccess(stand.transform.position, 0f, false)) return;
                var zdo = view.GetZDO();
                bool hidden = false;
                for (int extra = ArmorStandStorage.Utility; extra <= ArmorStandStorage.Trinket; extra++)
                {
                    var item = ArmorStandStorage.Extra(zdo, extra, out bool unreadable);
                    hidden |= item != null || unreadable;
                }
                var text = new StringBuilder();
                // The game offers E only for items its stand shows.
                if (hidden && stand.GetNrOfAttachedItems() == 0) text.Append("\n[<color=yellow><b>$KEY_Use</b></color>] $piece_itemstand_take");
                text.Append("\n[<color=yellow><b>Left Alt + $KEY_Use</b></color>] Swap equipment");
                __result += Localization.instance.Localize(text.ToString());
            }
        }

        private static string Name(ItemDrop.ItemData item) => Localization.instance.Localize(item.m_shared.m_name);

        private static void Exchange(ArmorStand stand, ZNetView view, Player player)
        {
            if (player.IsDead() || !PrivateArea.CheckAccess(stand.transform.position, 0f, false)) return;
            var positions = new Position[Wearables.Length];
            string refusal = Plan(stand, view.GetZDO(), player, positions);
            if (refusal != null) { player.Message(MessageHud.MessageType.Center, refusal); return; }
            var changed = positions.Where(position => position != null).ToArray();
            if (changed.Length == 0) return;
            var zdo = view.GetZDO();
            var inventory = player.GetInventory();
            // Queued equipment changes for the changed positions, of the outgoing pieces or of others, would later undo the
            // exchange; other queued actions stay.
            ActionQueue(player).RemoveAll(action => action.m_type != Player.MinorActionData.ActionType.Reload && action.m_item != null &&
                Array.IndexOf(Wearables, action.m_item.m_shared.m_itemType) >= 0 && positions[Array.IndexOf(Wearables, action.m_item.m_shared.m_itemType)] != null);
            // As with the stand's own attachment, one item of a worn stack, such as stacked Wishbones, hangs up.
            foreach (var position in changed)
                if (position.Worn != null) { player.UnequipItem(position.Worn); inventory.RemoveOneItem(position.Worn); }
            foreach (var position in changed)
            {
                if (position.From >= 0 && position.From != position.To) ArmorStandStorage.SetNative(view, position.From, null);
                if (position.FromExtra >= 0 && position.FromExtra != position.ToExtra) ArmorStandStorage.SetExtra(zdo, position.FromExtra, null);
            }
            foreach (var position in changed)
            {
                if (position.To >= 0) ArmorStandStorage.SetNative(view, position.To, position.Worn);
                if (position.ToExtra >= 0) ArmorStandStorage.SetExtra(zdo, position.ToExtra, position.Worn);
            }
            foreach (var position in changed)
                if (position.Stored != null) Place(inventory, position.Stored, position.Cell);
            if (changed.All(position => position.Stored == null || player.EquipItem(position.Stored)))
            {
                stand.m_effects.Create(stand.transform.position, Quaternion.identity);
                return;
            }
            foreach (var position in changed)
                if (position.Stored != null) { player.UnequipItem(position.Stored, false); inventory.RemoveItem(position.Stored); }
            foreach (var position in changed)
            {
                if (position.To >= 0) ArmorStandStorage.SetNative(view, position.To, null);
                if (position.ToExtra >= 0) ArmorStandStorage.SetExtra(zdo, position.ToExtra, null);
            }
            foreach (var position in changed)
            {
                if (position.From >= 0) ArmorStandStorage.SetNative(view, position.From, position.Stored);
                if (position.FromExtra >= 0) ArmorStandStorage.SetExtra(zdo, position.FromExtra, position.Stored);
                if (position.Worn == null) continue;
                if (inventory.ContainsItem(position.Worn)) { position.Worn.m_stack++; Changed(inventory, false, false); }
                else Place(inventory, position.Worn, position.WornCell);
                player.EquipItem(position.Worn, false);
            }
            player.Message(MessageHud.MessageType.Center, "The game refused a piece, so the exchange was undone.");
        }

        // Puts the exact item in the exact cell, where the game's own adding would merge a stackable piece into a stack.
        private static void Place(Inventory inventory, ItemDrop.ItemData item, Vector2i cell)
        {
            item.m_gridPos = cell;
            Items(inventory).Add(item);
            Changed(inventory, false, false);
        }

        // Fills the positions the exchange changes, or explains why it cannot happen.
        private static string Plan(ArmorStand stand, ZDO zdo, Player player, Position[] positions)
        {
            for (int index = 0; index < stand.m_slots.Count; index++)
            {
                var item = ArmorStandStorage.Native(zdo, index, out bool unreadable);
                if (unreadable && stand.m_slots[index].m_supportedTypes.Any(type => Array.IndexOf(Wearables, type) >= 0))
                    return "The armor stand holds an item this game does not know.";
                int wearable = item == null ? -1 : Array.IndexOf(Wearables, item.m_shared.m_itemType);
                if (wearable < 0) continue;
                if (positions[wearable] != null) return "The armor stand holds two " + Wearables[wearable].ToString().ToLowerInvariant() + " items; take them with E first.";
                positions[wearable] = new Position { Stored = item, From = index };
            }
            for (int extra = ArmorStandStorage.Utility; extra <= ArmorStandStorage.Trinket; extra++)
            {
                var item = ArmorStandStorage.Extra(zdo, extra, out bool unreadable);
                if (unreadable) return "The armor stand holds an unreadable " + ArmorStandStorage.ExtraType(extra).ToString().ToLowerInvariant() + " record.";
                if (item == null) continue;
                int wearable = Array.IndexOf(Wearables, item.m_shared.m_itemType);
                if (positions[wearable] != null) return "The armor stand holds two " + Wearables[wearable].ToString().ToLowerInvariant() + " items; take them with E first.";
                positions[wearable] = new Position { Stored = item, FromExtra = extra };
            }
            for (int wearable = 0; wearable < Wearables.Length; wearable++)
            {
                var worn = Worn[wearable](player);
                if (worn == null) continue;
                var position = positions[wearable] ?? (positions[wearable] = new Position());
                position.Worn = worn;
                position.WornCell = worn.m_gridPos;
                position.To = ArmorStandStorage.NativeSlot(stand, worn);
                if (position.To < 0) position.ToExtra = ArmorStandStorage.ExtraFor(stand, worn);
                if (position.To < 0 && position.ToExtra < 0) return Name(worn) + " cannot hang on an armor stand.";
                // Every stored wearable leaves the stand, so only something else, or another worn piece the stand shows in
                // the same place, can keep a native position taken.
                var other = position.To >= 0 ? ArmorStandStorage.NativeType(zdo, position.To) : ItemDrop.ItemData.ItemType.None;
                if (other != ItemDrop.ItemData.ItemType.None && Array.IndexOf(Wearables, other) < 0 ||
                    position.To >= 0 && positions.Take(wearable).Any(earlier => earlier?.Worn != null && earlier.To == position.To))
                    return "The armor stand's place for " + Name(worn) + " is taken.";
            }
            if (positions.Any(position => position?.Stored != null) && (player.InAttack() || player.InDodge() || player.IsSwimming() && !player.IsOnGround()))
                return "Equipment cannot change right now.";
            // The game's own refusals to wear a piece.
            for (int wearable = 0; wearable < Wearables.Length; wearable++)
            {
                var item = positions[wearable]?.Stored;
                if (item == null) continue;
                if (item.m_shared.m_useDurability && item.m_durability <= 0f) return Name(item) + " is broken.";
                if (item.m_shared.m_dlc.Length > 0 && !DLCMan.instance.IsDLCInstalled(item.m_shared.m_dlc)) return Name(item) + " needs " + item.m_shared.m_dlc + ".";
                if (wearable >= 4 && Game.m_worldLevel > 0 && item.m_worldLevel < Game.m_worldLevel) return Name(item) + " is from a lower world level.";
            }
            // A stored piece takes the cell of the worn piece it replaces, unless the rest of a worn stack stays there, and
            // otherwise its equipment slot or a free bag cell.
            var inventory = player.GetInventory();
            bool Leaves(Position position) => position?.Worn != null && position.Worn.m_stack == 1;
            var taken = new HashSet<Vector2i>(inventory.GetAllItems().Where(item => !positions.Any(position => Leaves(position) && position.Worn == item)).Select(item => item.m_gridPos));
            foreach (var position in positions)
                if (position?.Stored != null && Leaves(position)) taken.Add(position.Cell = position.WornCell);
            int rows = EquipmentAndQuickSlots.VisibleRows?.Invoke() ?? inventory.GetHeight();
            for (int wearable = 0; wearable < Wearables.Length; wearable++)
            {
                var position = positions[wearable];
                if (position?.Stored == null || Leaves(position)) continue;
                if (!EquipmentAndQuickSlots.TryEquipmentCell(Wearables[wearable], out var cell) || taken.Contains(cell))
                {
                    // The game fills armor from the bottom row and utility items and trinkets from the top.
                    bool top = wearable >= 4;
                    cell = new Vector2i(-1, -1);
                    for (int row = 0; row < rows && cell.x < 0; row++)
                        for (int column = 0; column < inventory.GetWidth() && cell.x < 0; column++)
                            if (!taken.Contains(new Vector2i(column, top ? row : rows - 1 - row))) cell = new Vector2i(column, top ? row : rows - 1 - row);
                    if (cell.x < 0) return "Your inventory has no room for " + Name(position.Stored) + ".";
                }
                taken.Add(position.Cell = cell);
            }
            return null;
        }
    }
}
