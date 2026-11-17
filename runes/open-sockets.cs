using System;
using System.Reflection;
using EpicLoot.ShardStones;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot shows the sockets of one item at a time in a shard slot panel of its own, beside the bag, and keeps that
    // item, its socket row and the panel in ShardUI's own fields.
    internal static class OpenSockets
    {
        private static readonly AccessTools.FieldRef<ItemDrop.ItemData> item =
            AccessTools.StaticFieldRefAccess<ItemDrop.ItemData>(AccessTools.Field(typeof(ShardUI), "_item"));
        private static readonly AccessTools.FieldRef<Inventory> row =
            AccessTools.StaticFieldRefAccess<Inventory>(AccessTools.Field(typeof(ShardUI), "_slots"));
        private static readonly FieldInfo panel = AccessTools.Field(typeof(ShardUI), "_panel");
        private static readonly PropertyInfo panelGrid = AccessTools.Property(panel.FieldType, "Grid") is var grid && grid?.PropertyType == typeof(InventoryGrid)
            ? grid : throw new MissingMemberException("ShardSlotPanel", "Grid");

        // The item whose sockets are open, its socket row and the grid showing that row, or null with no panel open.
        internal static ItemDrop.ItemData Item => ShardUI.IsOpen ? item() : null;
        internal static Inventory Row => ShardUI.IsOpen ? row() : null;
        internal static InventoryGrid Grid => ShardUI.IsOpen ? (InventoryGrid)panelGrid.GetValue(panel.GetValue(null)) : null;
    }
}
