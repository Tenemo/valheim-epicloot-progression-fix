using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using EpicLoot;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    internal static class CraftingFavoritesIndicator
    {
        private static AccessTools.FieldRef<bool> favoritesOnly;
        private static AccessTools.FieldRef<Button> favoritesButton;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.AaaCrafting, out var plugin)) return;
            var manager = plugin.Instance.GetType().Assembly.GetType(
                "AzuAntiArthriticCrafting.RecipeTracking.Favoriting.FavoriteManager", true);
            favoritesOnly = AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(manager, "favoritesOnly"));
            favoritesButton = AccessTools.StaticFieldRefAccess<Button>(AccessTools.Field(manager, "favoritingTogglingButton"));
            harmony.Patch(AccessTools.Method(manager, "ToggleFavoritesOnlyMode", Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(CraftingFavoritesIndicator), nameof(RefreshIndicator)));
            harmony.Patch(AccessTools.Method(manager, "BuildRightFilterRow", new[] { typeof(InventoryGui) }),
                postfix: new HarmonyMethod(typeof(CraftingFavoritesIndicator), nameof(ConfigureButton)));
            log.LogInfo("AAA_Crafting's favorites star highlights the actual filter state for clicks and keyboard shortcuts.");
        }

        private static void ConfigureButton()
        {
            var button = favoritesButton();
            if (button == null) return;
            // Recreated filters clone the repair button, which may currently be disabled.
            button.interactable = true;
            // Run after AAA_Crafting's generic click listener, which independently toggles the glow.
            button.onClick.RemoveListener(RefreshIndicator);
            button.onClick.AddListener(RefreshIndicator);
            RefreshIndicator();
        }

        private static void RefreshIndicator()
        {
            var button = favoritesButton();
            if (button == null) return;
            var glow = button.transform.Find("Glow");
            if (glow != null) glow.gameObject.SetActive(favoritesOnly());
        }
    }

    internal static class CraftingUncraftedFilter
    {
        private static AccessTools.FieldRef<RectTransform> filterRow;
        private static MethodInfo createButton, filterChanged;
        private static Button button;
        private static bool uncraftedOnly, stationTracked;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.AaaCrafting, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var manager = assembly.GetType("AzuAntiArthriticCrafting.RecipeTracking.Favoriting.FavoriteManager", true);
            filterRow = AccessTools.StaticFieldRefAccess<RectTransform>(AccessTools.Field(manager, "_rightFilterRow"));
            createButton = AccessTools.Method(manager, "CreateIconMiniButton");
            filterChanged = AccessTools.Method(assembly.GetType("AzuAntiArthriticCrafting.Utilities.Utilities", true), "OnFilterChanged");
            if (createButton == null || filterChanged == null)
                throw new InvalidOperationException("AAA_Crafting's filter buttons moved.");
            harmony.Patch(AccessTools.Method(manager, "BuildRightFilterRow", new[] { typeof(InventoryGui) }),
                postfix: new HarmonyMethod(typeof(CraftingUncraftedFilter), nameof(Create)));
            // Filter the complete recipe list before AAA_Crafting sorts, caches and paginates it.
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "UpdateRecipeList"),
                prefix: new HarmonyMethod(typeof(CraftingUncraftedFilter), nameof(Filter))
                    { priority = Priority.First, before = new[] { PluginIds.AaaCrafting } });
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "UpdateRecipe"),
                postfix: new HarmonyMethod(typeof(CraftingUncraftedFilter), nameof(Refresh)));
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "DoCrafting"),
                postfix: new HarmonyMethod(typeof(CraftingUncraftedFilter), nameof(FinishedCraft)) { priority = Priority.Last });
            log.LogInfo("The crafting filter row can show only recipes this character hasn't crafted yet.");
        }

        private static void Create(InventoryGui __0)
        {
            if (button != null) return;
            button = (Button)createButton.Invoke(null, new object[] { __0, filterRow(), "UncraftedOnly",
                CraftedAnvil.OutlineSprite, "Uncrafted recipes.",
                "Uncrafted only", false, (UnityAction)Toggle, false });
            // The factory clones the repair button, which may be disabled when no carried item needs repairs.
            button.interactable = true;
            button.transform.SetSiblingIndex(1);
            // AAA_Crafting's generic glow listener toggles independently of filter state.
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(Toggle);
            Refresh(__0);
        }

        private static void Toggle()
        {
            uncraftedOnly = !uncraftedOnly;
            filterChanged.Invoke(null, null);
            Refresh(InventoryGui.instance);
        }

        private static void Refresh(InventoryGui __instance)
        {
            if (button == null) return;
            bool shown = __instance.InCraftTab() && Eligibility.CraftingMode && stationTracked;
            if (button.gameObject.activeSelf != shown) button.gameObject.SetActive(shown);
            var glow = button.transform.Find("Glow");
            if (glow != null && glow.gameObject.activeSelf != uncraftedOnly) glow.gameObject.SetActive(uncraftedOnly);
        }

        private static void Filter(InventoryGui __instance, ref List<Recipe> recipes)
        {
            if (!__instance.InCraftTab() || !Eligibility.CraftingMode) return;
            // A station with nothing to track keeps its whole list, and Refresh hides the button.
            stationTracked = recipes.Any(CraftedAnvil.Tracked);
            if (!uncraftedOnly || !stationTracked) return;
            // Use the badge's exact rules, including legacy credits and cooking-station casts.
            // Keep the game's source list intact so toggling off restores every recipe.
            recipes = recipes.Where(recipe => CraftedAnvil.Tracked(recipe) && !CraftingHistory.IsUnlocked(recipe.m_item.gameObject.name)).ToList();
        }

        private static void FinishedCraft(InventoryGui __instance, Recipe ___m_craftRecipe, ItemDrop.ItemData ___m_craftUpgradeItem)
        {
            if (!uncraftedOnly || !__instance.InCraftTab() || !Eligibility.CraftingMode || ___m_craftUpgradeItem != null ||
                !CraftedAnvil.Tracked(___m_craftRecipe) || !CraftingHistory.IsUnlocked(___m_craftRecipe.m_item.gameObject.name)) return;
            // Native crafting records history after refreshing recipes. Refresh again after that credit, and stop the
            // completed batch before AAA_Crafting can return early on an empty list or queue a different recipe.
            CraftingAmountReset.StopQueuedCrafts();
            filterChanged.Invoke(null, null);
        }
    }
}
