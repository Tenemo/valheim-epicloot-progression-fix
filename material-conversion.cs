using System;
using System.Collections.Generic;
using System.Linq;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(MultiSelectItemListElement), "OnClicked")]
    internal static class MaterialConversionMaxQuantity
    {
        private static bool Prefix(MultiSelectItemListElement __instance, bool ____locked)
        {
            if (!(ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl))) return true;
            var panel = __instance.GetComponentInParent<ConvertUI>();
            if (panel == null ||
                !__instance.transform.IsChildOf(panel.AvailableItems.ListContainer) ||
                !(__instance.GetListElement() is ConversionRecipeUnity recipe)) return true;
            if (____locked || __instance.ReadOnly) return false;

            // Conversion rows have NoMax set, so their usual SelectMaxQuantity selects only one.
            // Use the actual conversion's providers and costs, reserving the other selections' materials.
            var others = panel.AvailableItems.GetSelectedItems<ConversionRecipeUnity>()
                .Where(selected => panel.AvailableItems.Multiselect && !ReferenceEquals(selected.Item1, recipe)).ToList();
            // Two selected conversions can need the same material.
            var reserved = ConvertUI.GetConversionCost(others).GroupBy(cost => cost.Item.m_shared.m_name)
                .ToDictionary(group => group.Key, group => group.Sum(cost => cost.Item.m_stack));
            var single = new List<Tuple<ConversionRecipeUnity, int>> { Tuple.Create(recipe, 1) };
            int maximum = 999; // EpicLoot's quantity input limit.
            foreach (var cost in ConvertUI.GetConversionCost(single))
            {
                var item = cost.Item;
                if (item.m_stack <= 0) continue;
                reserved.TryGetValue(item.m_shared.m_name, out int used);
                int available = Math.Max(0, InventoryManagement.Instance.CountItem(item) - used);
                maximum = Math.Min(maximum, available / item.m_stack);
            }
            __instance.SelectQuantity(__instance.GetSelectedQuantity() == maximum ? 0 : maximum, false);
            return false;
        }
    }

    [HarmonyPatch(typeof(ConvertUI), "OnEnable")]
    internal static class MaterialConversionMultiselect
    {
        private static void Prefix(ConvertUI __instance)
        {
            var list = __instance.AvailableItems;
            list.Multiselect = true;
            if (list.SelectAllToggle != null) list.SelectAllToggle.gameObject.SetActive(true);
        }
    }
}
