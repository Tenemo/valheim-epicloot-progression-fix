using System;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace EpicLootProgressionFix
{
    // EquipmentAndQuickSlots' count of the inventory rows players see and the cells of its equipment slots, which sit below
    // them with its quick slots. Unavailable without the mod.
    internal static class EquipmentAndQuickSlots
    {
        internal static Func<int> VisibleRows;
        private static Func<string, string> slotInfo;
        internal static void Initialize()
        {
            if (!PluginIds.TryGet(PluginIds.EquipmentAndQuickSlots, out var plugin)) return;
            var api = plugin.Instance.GetType().Assembly.GetType("EquipmentAndQuickSlots.API", true);
            VisibleRows = AccessTools.MethodDelegate<Func<int>>(AccessTools.Method(api, "GetVisibleRows"));
            slotInfo = AccessTools.MethodDelegate<Func<string, string>>(AccessTools.Method(api, "GetSlotInfoJson"));
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
    }
}
