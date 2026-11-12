using System.Globalization;
using EpicLoot;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Each row of the Sacrifice list carries the stars of its item's rarest enchant at the right end of its name box. A row
    // without a quantity spinner has a name box reaching the row's right edge, so long names get that room too; a
    // stackable row keeps its box, which ends before the spinner. A name that reaches the stars has them right after it.
    [HarmonyPatch(typeof(MultiSelectItemListElement), nameof(MultiSelectItemListElement.SetItem))]
    internal static class SacrificeRowStars
    {
        // The name box's right edge on a row without a spinner, as EpicLoot places its enchant names across a row.
        private const float RowEdge = -5f;
        // The least room between a name and its stars at the edge.
        private const float Gap = 8f;

        private static void Postfix(MultiSelectItemListElement __instance)
        {
            var name = __instance.ItemName;
            var item = __instance.GetListElement()?.GetItem();
            if (name == null || item == null || __instance.ReadOnly) return;
            var panel = __instance.GetComponentInParent<SacrificeUI>();
            if (panel == null || !__instance.transform.IsChildOf(panel.AvailableItems.ListContainer)) return;
            // Sorting and refreshing reuse rows for other items, so every row sets its box for the item it now shows.
            var box = panel.AvailableItems.ElementPrefab.ItemName.rectTransform.offsetMax;
            name.rectTransform.offsetMax = item.m_shared.m_maxStackSize == 1 ? new Vector2(RowEdge, box.y) : box;
            var stars = MagicEffectRarity.Decorate(MagicEffectRarity.GetBestTier(item.GetMagicItem()), "", false).TrimStart();
            if (stars.Length == 0) return;
            var text = Localization.instance.Localize(name.text);
            float width = name.rectTransform.rect.width - name.margin.x - name.margin.z;
            float start = width - name.GetPreferredValues(stars).x;
            if (width <= 0f || name.GetPreferredValues(text).x + Gap > start) { name.text = text + " " + stars; return; }
            // Rounded down, so the stars never reach past the edge and wrap.
            float percent = Mathf.Floor(start / width * 1000f) / 10f;
            name.text = text + "<pos=" + percent.ToString("0.#", CultureInfo.InvariantCulture) + "%>" + stars;
        }
    }
}
