using System;
using System.Linq;
using System.Reflection;
using EpicLoot;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    // The crafting window's item details show in their top right corner whether this character has crafted the selected
    // recipe, using the same check as the loot rolls: a gray anvil when it has, an outline while it has not. Hovering names
    // the state and, for equipment, what it means for enchanted drops. Recipes at the food and mead stations show the state
    // alone. Everything else, and the other gating modes, show nothing.
    internal static class CraftedAnvil
    {
        private const float Size = 26f, Margin = 6f;
        private const string CraftedTopic = "Crafted";
        private const string CraftedText = "It can drop as enchanted loot for you.";
        private const string UncraftedTopic = "Not crafted yet";
        private const string UncraftedText = "Craft once to unlock enchanted drops.";
        // The cauldron, food preparation table and mead ketill. mod-compatibility fails if one of them stops matching a recipe.
        private static readonly string[] FoodStations = { "$piece_cauldron", "$piece_preptable", "$piece_meadcauldron" };
        private static readonly Color Ink = new Color(0.024f, 0.02f, 0.016f, 1f);
        private static readonly Color CraftedGray = new Color(0.5f, 0.5f, 0.5f, 1f);
        private const string Feature = "Crafted-item anvil";
        private static FieldInfo selection;
        private static PropertyInfo selectedRecipe;
        private static Sprite filled, outline;
        private static Image anvil;
        private static UITooltip tooltip;

        internal static Sprite OutlineSprite => outline ??= Draw(false);

        private static bool Enchantable(Recipe recipe) => EpicLoot.EpicLoot.IsAllowedMagicItemType(recipe.m_item.m_itemData) &&
            !LootDenyList.IsDenied(recipe.m_item.gameObject.name);

        internal static bool Tracked(Recipe recipe) => recipe != null && recipe.m_item != null && (Enchantable(recipe) ||
            recipe.m_craftingStation != null && FoodStations.Contains(recipe.m_craftingStation.m_name));

        internal static void Initialize(IntegrationPatches harmony)
        {
            selection = AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");
            selectedRecipe = selection?.FieldType.GetProperty("Recipe");
            if (selectedRecipe?.PropertyType != typeof(Recipe))
                throw new InvalidOperationException("The game's recipe selection moved.");
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "Awake"), postfix: new HarmonyMethod(typeof(CraftedAnvil), nameof(Create)));
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "UpdateRecipe"), postfix: new HarmonyMethod(typeof(CraftedAnvil), nameof(Refresh)));
        }

        private static void Create(InventoryGui __instance)
        {
            // The inventory's item tooltip has a topic line above its text; the crafting window's own tooltips have only text.
            var slot = __instance.m_playerGrid.m_elementPrefab.GetComponent<UITooltip>();
            var prefab = slot == null ? null : slot.m_tooltipPrefab;
            if (prefab == null || !prefab.GetComponentsInChildren<TMP_Text>(true).Any(text => text.name == "Topic"))
            {
                DisabledFeatures.Set(Feature, "the inventory's item tooltip moved");
                return;
            }
            DisabledFeatures.Set(Feature, null);
            if (filled == null)
            {
                filled = Draw(true);
                outline = OutlineSprite;
            }
            var badge = new GameObject("CraftedAnvil", typeof(RectTransform), typeof(Image), typeof(UITooltip));
            var rect = (RectTransform)badge.transform;
            rect.SetParent(__instance.m_recipeName.transform.parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
            rect.anchoredPosition = new Vector2(-Margin, -Margin);
            rect.sizeDelta = new Vector2(Size, Size);
            anvil = badge.GetComponent<Image>();
            anvil.color = Ink;
            anvil.preserveAspect = true;
            tooltip = badge.GetComponent<UITooltip>();
            tooltip.m_tooltipPrefab = prefab;
            badge.SetActive(false);
        }

        private static void Refresh(InventoryGui __instance)
        {
            if (anvil == null) return;
            var recipe = (Recipe)selectedRecipe.GetValue(selection.GetValue(__instance));
            bool shown = Eligibility.CraftingMode && Tracked(recipe);
            if (anvil.gameObject.activeSelf != shown) anvil.gameObject.SetActive(shown);
            if (!shown) return;
            // Stay over the panel's later children, such as AAA_Crafting's description scroll view, for drawing and hovering.
            var transform = anvil.transform;
            if (transform.GetSiblingIndex() != transform.parent.childCount - 1) transform.SetAsLastSibling();
            bool crafted = CraftingHistory.IsUnlocked(recipe.m_item.gameObject.name);
            var sprite = crafted ? filled : outline;
            string text = !Enchantable(recipe) ? "" : crafted ? CraftedText : UncraftedText;
            if (anvil.sprite == sprite && tooltip.m_text == text) return;
            anvil.sprite = sprite;
            anvil.color = crafted ? CraftedGray : Ink;
            tooltip.Set(crafted ? CraftedTopic : UncraftedTopic, text);
        }

        private static Sprite Draw(bool solid)
        {
            const int pixels = 64, samples = 4;
            var texture = new Texture2D(pixels, pixels, TextureFormat.RGBA32, true)
                { name = solid ? "CraftedAnvil" : "UncraftedAnvil", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var colors = new Color32[pixels * pixels];
            for (int y = 0; y < pixels; y++)
            for (int x = 0; x < pixels; x++)
            {
                int covered = 0;
                for (int sampleY = 0; sampleY < samples; sampleY++)
                for (int sampleX = 0; sampleX < samples; sampleX++)
                {
                    float u = (x + (sampleX + 0.5f) / samples) / pixels, v = (y + (sampleY + 0.5f) / samples) / pixels;
                    if (Inside(u, v) && (solid || !Interior(u, v))) covered++;
                }
                colors[y * pixels + x] = new Color32(255, 255, 255, (byte)(255 * covered / (samples * samples)));
            }
            texture.SetPixels32(colors);
            texture.Apply(true, true);
            return Sprite.Create(texture, new Rect(0, 0, pixels, pixels), new Vector2(0.5f, 0.5f));
        }

        // An anvil seen from the side in the unit square: the face with its horn tapering to the left, a narrow waist,
        // a flared foot and a flat base.
        private static bool Inside(float x, float y)
        {
            if (y >= 0.6f && y <= 0.82f)
                return x >= 0.3f && x <= 0.96f || x >= 0.04f && x < 0.3f && y >= 0.75f - 0.15f * Mathf.Pow((x - 0.04f) / 0.26f, 1.5f);
            if (y >= 0.46f && y < 0.6f)
                return Between(x, (0.6f - y) / 0.14f, 0.4f, 0.49f, 0.86f, 0.77f);
            if (y >= 0.28f && y < 0.46f)
                return Between(x, (0.46f - y) / 0.18f, 0.49f, 0.38f, 0.77f, 0.88f);
            return y >= 0.18f && y < 0.28f && x >= 0.34f && x <= 0.92f;
        }

        private static bool Between(float x, float t, float leftFrom, float leftTo, float rightFrom, float rightTo) =>
            x >= Mathf.Lerp(leftFrom, leftTo, t) && x <= Mathf.Lerp(rightFrom, rightTo, t);

        // Points at least a stroke's width inside the anvil; the outline leaves them empty.
        private static bool Interior(float x, float y)
        {
            const float stroke = 0.07f;
            for (int step = 0; step < 8; step++)
            {
                float angle = step * Mathf.PI / 4f;
                if (!Inside(x + stroke * Mathf.Cos(angle), y + stroke * Mathf.Sin(angle))) return false;
            }
            return true;
        }
    }
}
