using System;
using System.Collections;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;

namespace EpicLootProgressionFix
{
    // AAA_Crafting 2.1.12 keeps the amount in its crafting box for the whole game session, so an amount set at one table
    // was still there at the next one. Closing the window resets it to 1. Changing recipes during a craft also cancels
    // the batch: AAA_Crafting otherwise starts each remaining craft with whatever recipe is currently selected.
    internal static class CraftingAmountReset
    {
        private static AccessTools.FieldRef<int> amount;
        private static AccessTools.FieldRef<TMP_InputField> amountBox;
        private static AccessTools.FieldRef<bool> wasCrafting, queueNextCraft;
        private static readonly FieldInfo Selection = AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");
        private static readonly FieldInfo Available = AccessTools.Field(typeof(InventoryGui), "m_availableRecipes");
        private static PropertyInfo selectedRecipe, selectedItem;
        private static bool completingCraft;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.AaaCrafting, out var plugin)) return;
            var craft = plugin.Instance.GetType().Assembly.GetType("AzuAntiArthriticCrafting.Patches.AAACraft");
            var amountField = craft == null ? null : AccessTools.Field(craft, "amount");
            var boxField = craft == null ? null : AccessTools.Field(craft, "inputAmount");
            var queue = craft?.GetNestedType("InventoryGui_UpdateRecipe_AAACraft", BindingFlags.Public | BindingFlags.NonPublic);
            var wasCraftingField = queue == null ? null : AccessTools.Field(queue, "wasCrafting");
            var queueField = queue == null ? null : AccessTools.Field(queue, "queueNextCraft");
            var selection = Selection?.FieldType;
            selectedRecipe = selection?.GetProperty("Recipe");
            selectedItem = selection?.GetProperty("ItemData");
            if (amountField == null || !amountField.IsStatic || amountField.FieldType != typeof(int) ||
                boxField == null || !boxField.IsStatic || boxField.FieldType != typeof(TMP_InputField) ||
                wasCraftingField == null || !wasCraftingField.IsStatic || wasCraftingField.FieldType != typeof(bool) ||
                queueField == null || !queueField.IsStatic || queueField.FieldType != typeof(bool) ||
                selectedRecipe?.PropertyType != typeof(Recipe) || selectedItem?.PropertyType != typeof(ItemDrop.ItemData) || Available == null)
                throw new InvalidOperationException("AAA_Crafting's crafting amount, queue or recipe selection moved.");
            amount = AccessTools.StaticFieldRefAccess<int>(amountField);
            amountBox = AccessTools.StaticFieldRefAccess<TMP_InputField>(boxField);
            wasCrafting = AccessTools.StaticFieldRefAccess<bool>(wasCraftingField);
            queueNextCraft = AccessTools.StaticFieldRefAccess<bool>(queueField);
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.Hide)),
                postfix: new HarmonyMethod(typeof(CraftingAmountReset), nameof(ResetAmount)));
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "SetRecipe", new[] { typeof(int), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(CraftingAmountReset), nameof(CancelChangedRecipe)));
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "DoCrafting"),
                prefix: new HarmonyMethod(typeof(CraftingAmountReset), nameof(BeginCraftCompletion)),
                finalizer: new HarmonyMethod(typeof(CraftingAmountReset), nameof(EndCraftCompletion)));
            log.LogInfo("AAA_Crafting resets its amount on close and cancels active batches when the recipe changes.");
        }

        private static void CancelChangedRecipe(InventoryGui __instance, int index, Recipe ___m_craftRecipe,
            ItemDrop.ItemData ___m_craftUpgradeItem, float ___m_craftTimer)
        {
            // SetRecipe retains the old struct when its recipe and item are unchanged, including stale CanCraft.
            // Keep the newly rebuilt row's affordability so the actual batch controls reflect replenished materials.
            if (index >= 0) Selection.SetValue(__instance, ((IList)Available.GetValue(__instance))[index]);
            if (___m_craftTimer < 0f && !wasCrafting() && !queueNextCraft()) return;
            var selected = Selection.GetValue(__instance);
            var item = (ItemDrop.ItemData)selectedItem.GetValue(selected);
            // Recipe lists refresh after each craft. Keep the batch when the same recipe and upgrade item are selected.
            // A completed upgrade replaces its item in the same inventory slot before refreshing the list.
            if ((Recipe)selectedRecipe.GetValue(selected) == ___m_craftRecipe &&
                (ReferenceEquals(item, ___m_craftUpgradeItem) || completingCraft && item != null && ___m_craftUpgradeItem != null &&
                    item.m_gridPos == ___m_craftUpgradeItem.m_gridPos)) return;
            // A selection can change inside UpdateRecipe after a craft finishes but before AAA_Crafting queues the next.
            wasCrafting() = false;
            queueNextCraft() = false;
            __instance.m_craftCancelButton.onClick.Invoke();
            ResetAmount();
        }

        private static void BeginCraftCompletion(out bool __state)
        {
            __state = completingCraft;
            completingCraft = true;
        }

        private static void EndCraftCompletion(bool __state) => completingCraft = __state;

        internal static void StopQueuedCrafts()
        {
            wasCrafting() = false;
            queueNextCraft() = false;
            ResetAmount();
        }

        // The game also calls Hide every frame while the player is dead or teleporting, so write only when needed.
        private static void ResetAmount()
        {
            if (amount() == 1) return;
            amount() = 1;
            var box = amountBox();
            if (box != null) box.SetTextWithoutNotify("1");
        }
    }
}
