using System.Text.RegularExpressions;
using EpicLoot;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Bag and chest slots show the stars of their item's rarest enchant, small and violet, in the slot's bottom-left corner;
    // the item's quality keeps the top-right corner. Runes keep that corner for their strength.
    [HarmonyPatch(typeof(API), nameof(API.ApplyMagicItemBackground))]
    internal static class EnchantSlotStars
    {
        private static void Postfix(GameObject slotRoot, ItemDrop.ItemData item, bool inventoryGrid)
        {
            if (!inventoryGrid || !slotRoot || item == null) return;
            int tier = RuneStacks.IsRune(item) ? 0 : MagicEffectRarity.GetBestTier(item.GetMagicItem());
            if (!slotRoot.TryGetComponent<EnchantSlotStarLabel>(out var stars))
            {
                if (tier == 0) return;
                stars = slotRoot.AddComponent<EnchantSlotStarLabel>();
            }
            stars.Want(tier);
        }
    }

    internal sealed class EnchantSlotStarLabel : MonoBehaviour
    {
        // Text size and the gap from the slot's left and bottom edges.
        private const float Size = 10f, Left = 1f, Bottom = 0f;
        private static readonly Color Violet = new Color32(0xC7, 0x9B, 0xFF, 0xFF);
        private TMP_Text label;
        private int wanted, frame = -1, shown;

        internal void Want(int tier)
        {
            wanted = tier;
            frame = Time.frameCount;
        }

        // The grid draws every filled slot each frame, so a slot it didn't draw this frame is empty. The label changes only
        // when the slot's tier does.
        private void LateUpdate()
        {
            int tier = frame == Time.frameCount ? wanted : 0;
            if (tier == shown) return;
            shown = tier;
            // EpicLoot's star glyphs without its tier colour; every tier is violet here.
            string stars = tier > 0 ? Regex.Replace(MagicEffectRarity.Decorate(tier, "", false), "<[^>]*>", "").Trim() : "";
            if (label == null)
            {
                if (stars.Length == 0) return;
                label = Create();
            }
            label.text = stars;
            label.gameObject.SetActive(stars.Length > 0);
        }

        private TMP_Text Create()
        {
            var template = GetComponent<InventoryElement>().m_amount;
            var text = Instantiate(template, transform);
            text.name = Plugin.Id + ".enchant-stars";
            text.raycastTarget = false;
            text.enabled = true;
            text.enableAutoSizing = false;
            text.fontSize = Size;
            text.characterSpacing = -4f;
            text.color = Violet;
            text.alignment = TextAlignmentOptions.BottomLeft;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.margin = Vector4.zero;
            var rect = text.rectTransform;
            rect.localScale = Vector3.one;
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(Left, Bottom);
            rect.sizeDelta = new Vector2(40f, Size + 4f);
            return text;
        }
    }
}
