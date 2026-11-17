using System.Collections.Generic;
using System.Linq;
using EpicLoot;
using EpicLoot.Crafting;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Set Etch takes exactly one set rune. EpicLoot still gave its rune rows a quantity box that selected the whole stack,
    // and every set rune is an etched runestone of its rarity, so a name count put every rune of that rarity on the cost
    // tile. Rows now show the one rune they use against all the runes of that set the table can take, and the cost tile
    // counts those same runes.
    internal static class SetRuneCounts
    {
        // The runes Set Etch can spend: set runes of that set, without the favorites it never consumes.
        internal static int Count(ItemDrop.ItemData rune, IEnumerable<ItemDrop.ItemData> items)
        {
            string set = rune.GetMagicItem()?.SetID;
            return items.Where(item => item != null && item.IsSetRune() && item.GetMagicItem()?.SetID == set &&
                !EpicLoot.Compatibility.ItemFavorites.IsProtected(item)).Sum(item => item.m_stack);
        }

        internal static int Available(ItemDrop.ItemData rune) => Count(rune, InventoryManagement.Instance.GetAllItems());

        internal static bool IsSetEtchRow(MultiSelectItemListElement element)
        {
            if (element.ReadOnly || element.GetListElement()?.GetItem()?.IsSetRune() != true) return false;
            var list = element.GetComponentInParent<MultiSelectItemList>(true);
            return list != null && element.GetComponentInParent<RuneUI>(true)?.AvailableRunes == list;
        }

        [HarmonyPatch(typeof(MultiSelectItemListElement), nameof(MultiSelectItemListElement.SelectQuantity))]
        private static class OneRune
        {
            private static void Prefix(MultiSelectItemListElement __instance, ref int __0)
            {
                if (__0 > 1 && IsSetEtchRow(__instance)) __0 = 1;
            }
        }

        [HarmonyPatch(typeof(MultiSelectItemListElement), nameof(MultiSelectItemListElement.Refresh))]
        private static class UsedOfAvailable
        {
            private static void Postfix(MultiSelectItemListElement __instance)
            {
                if (!IsSetEtchRow(__instance)) return;
                foreach (var control in new Component[] { __instance.ItemSelectedQuantity, __instance.QuantityUpButton, __instance.QuantityDownButton })
                    if (control != null) control.gameObject.SetActive(false);
                if (__instance.ItemTotalQuantity == null) return;
                __instance.ItemTotalQuantity.gameObject.SetActive(true);
                __instance.ItemTotalQuantity.text = $"{(__instance.IsSelected() ? 1 : 0)} / {Available(__instance.GetListElement().GetItem())}";
            }
        }
    }
}
