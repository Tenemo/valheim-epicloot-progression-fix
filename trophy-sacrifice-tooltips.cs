using System;
using System.Collections.Generic;
using EpicLoot;
using EpicLoot.Crafting;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
    internal static class TrophySacrificeTooltips
    {
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Postfix(ItemDrop.ItemData item, ref string __result)
        {
            if (item?.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Trophy) return;
            var products = EnchantCostsHelper.GetSacrificeProducts(item);
            if (products == null) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rewards = new List<string>();
            foreach (var product in products)
            {
                string kind = product.Item.StartsWith("Shard", StringComparison.Ordinal) ? "Shard" :
                    product.Item.StartsWith("Runestone", StringComparison.Ordinal) ? "Runestone" : null;
                if (kind == null || !Enum.TryParse(product.Item.Substring(kind.Length), out ItemRarity rarity) ||
                    !Enum.IsDefined(typeof(ItemRarity), rarity) || product.Item != kind + rarity || !seen.Add(product.Item)) continue;
                var reward = ObjectDB.instance.GetItemPrefab(product.Item)?.GetComponent<ItemDrop>();
                if (reward == null) continue;
                string name = Localization.instance.Localize(reward.m_itemData.m_shared.m_name);
                rewards.Add("<color=" + global::EpicLoot.EpicLoot.GetRarityColor(rarity) + ">" + name + "</color>");
            }
            if (rewards.Count > 0)
            {
                string line = "\n\n" + Localization.instance.Localize("$mod_epicloot_sacrifice") + ": " + string.Join(", ", rewards);
                int weight = __result.IndexOf("\n$item_weight:", StringComparison.Ordinal);
                if (weight < 0) weight = __result.IndexOf("\n" + Localization.instance.Localize("$item_weight") + ":", StringComparison.Ordinal);
                int afterWeight = weight < 0 ? -1 : __result.IndexOf('\n', weight + 1);
                __result = afterWeight < 0 ? __result + line : __result.Insert(afterWeight, line);
            }
        }
    }
}
