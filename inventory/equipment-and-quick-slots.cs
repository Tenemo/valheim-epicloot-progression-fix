using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // EquipmentAndQuickSlots' count of the inventory rows players see and the cells of its equipment slots, which sit below
    // them with its quick slots. Unavailable without the mod.
    internal static class EquipmentAndQuickSlots
    {
        internal static Func<int> VisibleRows;
        private static Func<string, string> slotInfo;
        private static MethodInfo quickSlots, shortcut, fits;
        private static PropertyInfo active, cell;
        private static readonly KeyCode[] Modifiers =
            { KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift, KeyCode.RightShift };

        internal static void Initialize()
        {
            if (!PluginIds.TryGet(PluginIds.EquipmentAndQuickSlots, out var plugin)) return;
            var api = plugin.Instance.GetType().Assembly.GetType("EquipmentAndQuickSlots.API", true);
            VisibleRows = AccessTools.MethodDelegate<Func<int>>(AccessTools.Method(api, "GetVisibleRows"));
            slotInfo = AccessTools.MethodDelegate<Func<string, string>>(AccessTools.Method(api, "GetSlotInfoJson"));
            var slots = api.Assembly.GetType("EquipmentAndQuickSlots.Slots", true);
            var slot = slots.GetNestedType("Slot");
            quickSlots = AccessTools.Method(slots, "GetQuickSlots");
            shortcut = slot == null ? null : AccessTools.Method(slot, "GetShortcut");
            fits = slot == null ? null : AccessTools.Method(slot, "ItemFits");
            active = slot == null ? null : AccessTools.Property(slot, "IsActive");
            cell = slot == null ? null : AccessTools.Property(slot, "GridPosition");
            if (quickSlots == null || shortcut?.ReturnType != typeof(KeyboardShortcut) || fits == null || active == null || cell == null)
                throw new InvalidOperationException("EquipmentAndQuickSlots' quick slots moved.");
        }

        // The cell of the active equipment slot that holds a worn Helmet, Chest, Legs, Shoulder, Utility or Trinket item,
        // each named after its type.
        internal static bool TryEquipmentCell(ItemDrop.ItemData.ItemType type, out Vector2i cell)
        {
            cell = new Vector2i(-1, -1);
            string info = slotInfo?.Invoke(type.ToString());
            if (info == null) return false;
            var slot = JObject.Parse(info);
            if (!(bool)slot["active"] || !(bool)slot["isEquipmentSlot"]) return false;
            cell = new Vector2i((int)slot["gridX"], (int)slot["gridY"]);
            return true;
        }

        // The cell of the active quick slot whose hotkey, as set in EquipmentAndQuickSlots, went down this frame with exactly
        // the Alt, Ctrl and Shift keys it names, and whether that slot takes a given item.
        internal static bool TryPressedQuickSlot(out Vector2i slotCell, out Func<ItemDrop.ItemData, bool> takes)
        {
            slotCell = new Vector2i(-1, -1); takes = null;
            if (quickSlots == null || !Input.anyKeyDown) return false;
            foreach (var slot in (Array)quickSlots.Invoke(null, null))
            {
                if (!(bool)active.GetValue(slot)) continue;
                var keys = (KeyboardShortcut)shortcut.Invoke(slot, null);
                var named = keys.Modifiers.ToArray();
                if (keys.MainKey == KeyCode.None || !Input.GetKeyDown(keys.MainKey) || !named.All(Input.GetKey) ||
                    Modifiers.Any(key => !named.Contains(key) && Input.GetKey(key))) continue;
                slotCell = (Vector2i)cell.GetValue(slot);
                takes = item => (bool)fits.Invoke(slot, new object[] { item });
                return true;
            }
            return false;
        }
    }
}
