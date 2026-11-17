using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The automatic grave pickup and Take all add each grave item at its saved slot, but anything picked up since death
    // may hold that slot, and the grave item then goes to the first free cell. Just before your own grave's transfer,
    // each carried item in a grave slot moves to a free cell outside the grave's whole layout or, with none left, onto a
    // grave stack it joins whole, as the native fit check counts it. Other carried items stay. If that cannot place them
    // all, nothing moves and the grave keeps its items. Equipment and quick slots lie below the visible rows, so for each
    // inventory row bought since death the grave's slot items first move one row down with them.
    internal static class GraveSlotRecovery
    {
        private const string GraveRows = Plugin.Id + ".grave-rows";
        private static readonly Func<TombStone, bool> OwnGrave =
            AccessTools.MethodDelegate<Func<TombStone, bool>>(AccessTools.Method(typeof(TombStone), "IsOwner"));
        private static readonly Action<Inventory, bool, bool> Changed =
            AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(AccessTools.Method(typeof(Inventory), "Changed"));
        private static readonly Action<Container> Save = AccessTools.MethodDelegate<Action<Container>>(AccessTools.Method(typeof(Container), "Save"));
        // EquipmentAndQuickSlots' equipment and quick slot cells below the visible rows; without it every cell is ordinary.
        private static readonly Func<Vector2i, object> SlotAt;
        private static readonly Func<int> VisibleRows;
        private static readonly MethodInfo ItemFits;
        private static readonly PropertyInfo Active, QuickSlot, EquipmentSlot;

        static GraveSlotRecovery()
        {
            if (!PluginIds.TryGet(PluginIds.EquipmentAndQuickSlots, out var plugin)) return;
            var slots = plugin.Instance.GetType().Assembly.GetType("EquipmentAndQuickSlots.Slots", true);
            var slot = slots.GetNestedType("Slot");
            var slotAt = AccessTools.Method(slots, "GetSlotInGrid", new[] { typeof(Vector2i) });
            var visibleRows = AccessTools.PropertyGetter(slots, "VisibleRows");
            ItemFits = slot == null ? null : AccessTools.Method(slot, "ItemFits");
            Active = slot == null ? null : AccessTools.Property(slot, "IsActive");
            QuickSlot = slot == null ? null : AccessTools.Property(slot, "IsQuickSlot");
            EquipmentSlot = slot == null ? null : AccessTools.Property(slot, "IsEquipmentSlot");
            if (slotAt == null || visibleRows == null || ItemFits == null || Active == null || QuickSlot == null || EquipmentSlot == null)
                throw new InvalidOperationException("EquipmentAndQuickSlots' slot layout moved.");
            SlotAt = AccessTools.MethodDelegate<Func<Vector2i, object>>(slotAt);
            VisibleRows = AccessTools.MethodDelegate<Func<int>>(visibleRows);
        }

        [HarmonyPatch(typeof(TombStone), nameof(TombStone.Setup))]
        private static class RecordRows
        {
            private static void Postfix(long ownerUID, ZNetView ___m_nview)
            {
                if (VisibleRows != null && ___m_nview.IsValid() && Game.instance && ownerUID == Game.instance.GetPlayerProfile().GetPlayerID())
                    ___m_nview.GetZDO().Set(GraveRows, VisibleRows());
            }
        }

        [HarmonyPatch(typeof(Container), "RPC_TakeAllResponse")]
        private static class AutomaticPickup
        {
            [HarmonyPriority(Priority.Last)]
            private static bool Prefix(Container __instance, bool granted, bool __runOriginal) =>
                __runOriginal && (!granted || !Player.m_localPlayer || Prepare(__instance));
        }

        [HarmonyPatch(typeof(InventoryGui), "OnTakeAll")]
        private static class TakeAll
        {
            [HarmonyPriority(Priority.Last)]
            private static bool Prefix(Container ___m_currentContainer, bool __runOriginal) =>
                __runOriginal && (!___m_currentContainer || !Player.m_localPlayer || Player.m_localPlayer.IsTeleporting()
                    || Prepare(___m_currentContainer));
        }

        // False shows the inventory-full message and leaves the carried items and the grave unchanged.
        private static bool Prepare(Container container)
        {
            var grave = container.GetComponent<TombStone>();
            if (!grave || !OwnGrave(grave)) return true;
            // Graves from before the record keep their cells.
            var record = grave.GetComponent<ZNetView>().GetZDO();
            int deathRows = record.GetInt(GraveRows), bought = VisibleRows != null && deathRows > 0 ? VisibleRows() - deathRows : 0;
            if (bought > 0) Lower(container.GetInventory(), deathRows, bought);
            if (!MakeRoom(container))
            {
                if (bought > 0) Lower(container.GetInventory(), deathRows + bought, -bought);
                return false;
            }
            if (bought > 0)
            {
                record.Set(GraveRows, deathRows + bought);
                Save(container);
            }
            return true;
        }

        private static void Lower(Inventory grave, int firstSlotRow, int rows)
        {
            foreach (var item in grave.GetAllItems())
                if (item.m_gridPos.y >= firstSlotRow) item.m_gridPos = new Vector2i(item.m_gridPos.x, item.m_gridPos.y + rows);
        }

        private static bool MakeRoom(Container container)
        {
            var player = Player.m_localPlayer;
            var inventory = player.GetInventory();
            var layout = new Dictionary<Vector2i, ItemDrop.ItemData>();
            int unplaced = 0;
            foreach (var item in container.GetInventory().GetAllItems())
            {
                var cell = item.m_gridPos;
                if (cell.x >= 0 && cell.y >= 0 && cell.x < inventory.GetWidth() && cell.y < inventory.GetHeight()
                    && !layout.ContainsKey(cell) && Fits(cell, item)) layout[cell] = item;
                else unplaced++;
            }
            var blocking = inventory.GetAllItems().Where(item => layout.ContainsKey(item.m_gridPos))
                .OrderBy(item => item.m_gridPos.y).ThenBy(item => item.m_gridPos.x).ToList();
            if (blocking.Count == 0) return true;

            var free = new List<Vector2i>();
            for (int y = 0; y < inventory.GetHeight(); y++)
                for (int x = 0; x < inventory.GetWidth(); x++)
                {
                    var cell = new Vector2i(x, y);
                    if (!layout.ContainsKey(cell) && inventory.GetItemAt(x, y) == null && Ordinary(cell) && !RecallRecovery.Reserves(inventory, cell)) free.Add(cell);
                }
            // Grave items without a usable saved slot take free cells natively after the others land.
            int spare = free.Count - unplaced;
            var joining = new Dictionary<ItemDrop.ItemData, Vector2i>();
            foreach (var item in blocking)
            {
                if (blocking.Count - joining.Count <= spare) break;
                foreach (var home in layout)
                    if ((home.Key == item.m_gridPos || inventory.GetItemAt(home.Key.x, home.Key.y) == null)
                        && !joining.ContainsValue(home.Key) && Joins(item, home.Value))
                    {
                        joining[item] = home.Key;
                        break;
                    }
            }
            if (blocking.Count - joining.Count > spare)
            {
                player.Message(MessageHud.MessageType.Center, "$inventory_full");
                return false;
            }

            int next = 0;
            foreach (var item in blocking)
            {
                if (!joining.TryGetValue(item, out var cell)) cell = free[next++];
                // Worn armor cannot stay worn outside its slot; EquipmentAndQuickSlots re-equips the grave's own piece.
                if (cell != item.m_gridPos && player.IsItemEquiped(item) && InEquipmentSlot(item.m_gridPos)) player.UnequipItem(item, false);
                item.m_gridPos = cell;
            }
            Changed(inventory, false, false);
            return true;
        }

        // The native slot transfer stacks a grave item onto a matching carried one only when it fits whole there.
        private static bool Joins(ItemDrop.ItemData carried, ItemDrop.ItemData returning) =>
            carried.m_shared.m_maxStackSize > 1 && carried.IsSameType(returning)
            && carried.m_stack + returning.m_stack <= carried.m_shared.m_maxStackSize && RuneStacks.StacksWith(carried, returning);

        private static bool Fits(Vector2i cell, ItemDrop.ItemData item)
        {
            var slot = SlotAt?.Invoke(cell);
            return slot == null || (bool)ItemFits.Invoke(slot, new object[] { item });
        }

        // A visible inventory cell or an active quick slot, as native free-slot searches use them.
        private static bool Ordinary(Vector2i cell)
        {
            var slot = SlotAt?.Invoke(cell);
            return slot == null || (bool)QuickSlot.GetValue(slot) && (bool)Active.GetValue(slot);
        }

        private static bool InEquipmentSlot(Vector2i cell)
        {
            var slot = SlotAt?.Invoke(cell);
            return slot != null && (bool)EquipmentSlot.GetValue(slot);
        }
    }
}
