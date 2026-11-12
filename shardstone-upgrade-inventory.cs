using System.Collections.Generic;
using System.Linq;
using EpicLoot.CraftingV2;
using EpicLoot.ShardStones;
using EpicLoot_UnityLib;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Convert materials' Upgrade shardstones counted shardstones in nearby chests too, so it listed stones the player
    // does not carry and upgraded them out of chests. While that mode shows, it lists, counts and takes the stones being
    // upgraded only from the player's own bag, favorites included; the shards they cost still come from nearby chests.
    [HarmonyPatch]
    internal static class ShardstoneUpgradeInventory
    {
        private static ObjectDB namesFrom;
        private static HashSet<string> shardstones;

        private static bool Upgrading()
        {
            var table = EnchantingTableUI.instance;
            if (table == null || !table.gameObject.activeInHierarchy || Player.m_localPlayer == null) return false;
            foreach (var panel in table.Panels)
                if (panel is ConvertUI convert && convert.isActiveAndEnabled) return GameFields.ConversionMode(convert) == MaterialConversionType.ShardUpgrade;
            return false;
        }

        private static bool Shardstone(string name)
        {
            if (ObjectDB.instance == null) return false;
            if (namesFrom != ObjectDB.instance)
            {
                namesFrom = ObjectDB.instance;
                shardstones = new HashSet<string>(ObjectDB.instance.m_items.Select(prefab => prefab.GetComponent<ItemDrop>()?.m_itemData)
                    .Where(item => item != null && Shards.IsShard(item)).Select(item => item.m_shared.m_name));
            }
            return shardstones.Contains(name);
        }

        [HarmonyPatch(typeof(InventoryManagement), nameof(InventoryManagement.CountItem), typeof(string)), HarmonyPostfix]
        private static void CountCarried(string __0, ref int __result)
        {
            if (Upgrading() && Shardstone(__0)) __result = Player.m_localPlayer.GetInventory().CountItems(__0);
        }

        [HarmonyPatch(typeof(InventoryManagement), nameof(InventoryManagement.RemoveItem), typeof(string), typeof(int)), HarmonyPrefix]
        private static bool TakeCarried(string __0, int __1, ref int __result)
        {
            if (!Upgrading() || !Shardstone(__0)) return true;
            var inventory = Player.m_localPlayer.GetInventory();
            int before = inventory.CountItems(__0);
            inventory.RemoveItem(__0, __1);
            __result = before - inventory.CountItems(__0);
            return false;
        }
    }
}
