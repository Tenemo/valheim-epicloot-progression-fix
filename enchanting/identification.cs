using System;
using System.Collections.Generic;
using System.Linq;
using EpicLoot;
using EpicLoot.Crafting;
using EpicLoot.CraftingV2;
using EpicLoot_UnityLib;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot's preview lists raw loot-table entries; in crafting mode, list what the gate will actually hand out.
    // hasLoot enables the Identify button, so it follows the same list.
    [HarmonyPatch(typeof(EnchantingUIController), "GetPotentialItemRollsByCategory",
        new[] { typeof(string), typeof(List<ItemDrop.ItemData>), typeof(bool) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
    internal static class IdentificationPreview
    {
        private static readonly Func<string, IdentifyTypeConfig> Category = AccessTools.MethodDelegate<Func<string, IdentifyTypeConfig>>(
            AccessTools.Method(typeof(EnchantingUIController), "SelectLootIdentifyDetails"));
        private static readonly Func<IdentifyTypeConfig, Heightmap.Biome, List<LootTable>> Tables =
            AccessTools.MethodDelegate<Func<IdentifyTypeConfig, Heightmap.Biome, List<LootTable>>>(
                AccessTools.Method(typeof(EnchantingUIController), "GetLootTablesForIdentifyStyle"));

        private static bool Prefix(string filter, List<ItemDrop.ItemData> itemsSelected, ref bool hasLoot,
            ref List<InventoryItemListElement> __result)
        {
            if (!Eligibility.CraftingMode) return true;
            hasLoot = false;
            __result = new List<InventoryItemListElement>();
            var category = Category(filter);
            if (category == null || Player.m_localPlayer == null) return false;
            var names = new HashSet<string>();
            foreach (var biome in itemsSelected.Where(x => x != null && x.m_dropPrefab != null)
                .Select(EnchantHelper.GetBiomeFromUnidentifiedItem).Distinct())
            {
                var tables = Tables(category, biome);
                foreach (var entry in LootRoller.GetLootTableChances(Player.m_localPlayer.transform.position, tables))
                {
                    if (entry.Value <= 0) continue;
                    foreach (string name in Eligibility.Candidates(entry.Key))
                    {
                        if (!names.Add(name)) continue;
                        var prefab = ObjectDB.instance.GetItemPrefab(name);
                        var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                        if (drop != null) __result.Add(new InventoryItemListElement { Item = drop.m_itemData });
                    }
                }
            }
            hasLoot = __result.Count > 0;
            return false;
        }
    }
}
