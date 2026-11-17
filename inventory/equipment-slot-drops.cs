using System;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EquipmentAndQuickSlots finishes a drag into or out of an equipment slot by finding the dropped item on its new cell
    // through the item's shared data, and only then equips or unequips it. The game stores a moved item as a copy, and
    // EpicLoot gives each Indestructible copy its own durability-free shared data, so the dropped piece was never found:
    // worn Indestructible gear dropped on a bag cell stayed worn and went straight back into its slot. Find it by the
    // shared data both copies were made from.
    [HarmonyPatch]
    internal static class EquipmentSlotDrops
    {
        private static MethodBase TargetMethod()
        {
            if (!PluginIds.TryGet(PluginIds.EquipmentAndQuickSlots, out var plugin)) throw new InvalidOperationException("EquipmentAndQuickSlots is not loaded.");
            var rules = plugin.Instance.GetType().Assembly.GetType("EquipmentAndQuickSlots.InventoryPatches+InventoryGui_OnSelectedItem_DragRules", true);
            return AccessTools.Method(rules, "LandedItem", new[] { typeof(Vector2i), typeof(ItemDrop.ItemData.SharedData) })
                ?? throw new MissingMethodException(rules.FullName, "LandedItem");
        }

        private static void Postfix(Vector2i __0, ItemDrop.ItemData.SharedData __1, ref ItemDrop.ItemData __result)
        {
            if (__result != null || __1 == null) return;
            var item = Player.m_localPlayer?.GetInventory()?.GetItemAt(__0.x, __0.y);
            if (item?.m_shared != null && CopiedIndestructibleItems.Original(item.m_shared) == CopiedIndestructibleItems.Original(__1)) __result = item;
        }
    }
}
