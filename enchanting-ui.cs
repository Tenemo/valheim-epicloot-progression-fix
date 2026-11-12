using System;
using EpicLoot;
using EpicLoot.Crafting;
using EpicLoot.CraftingV2;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(MultiSelectItemListElement), nameof(MultiSelectItemListElement.Refresh))]
    public sealed class EnchantingCostCounts : MonoBehaviour
    {
        private MultiSelectItemListElement element;
        private float nextRefresh;
        private string lastSummary = "";

        private static void Postfix(MultiSelectItemListElement __instance)
        {
            // Cost rows check affordability; inventory rows and output previews do not.
            if (!__instance.ReadOnly || !__instance.CheckPlayerInventory ||
                __instance.ItemTotalQuantity == null || __instance.GetListElement()?.GetItem() == null) return;

            var display = __instance.GetComponent<EnchantingCostCounts>();
            if (display == null)
            {
                display = __instance.gameObject.AddComponent<EnchantingCostCounts>();
                display.element = __instance;
                // Show the count at 1.5 times the prefab's base size, shrinking back toward it only when a long count
                // would not fit the tile on one line.
                var text = __instance.ItemTotalQuantity;
                text.fontSizeMax = Mathf.RoundToInt(text.fontSize * 1.5f);
                text.fontSizeMin = Mathf.Min(8f, text.fontSize);
                text.enableAutoSizing = true;
            }
            display.RefreshCount();
        }

        private void OnEnable() => nextRefresh = 0;

        private void Update()
        {
            if (Time.unscaledTime >= nextRefresh) RefreshCount();
        }

        internal void RefreshCount()
        {
            nextRefresh = Time.unscaledTime + 0.5f;
            if (element == null || !element.ReadOnly || !element.CheckPlayerInventory || Player.m_localPlayer == null) return;
            var entry = element.GetListElement();
            var item = entry?.GetItem();
            if (item == null || element.ItemTotalQuantity == null) return;

            // Use the same provider count as HasItem, including CraftyBoxes' range and pull filters.
            int available = InventoryManagement.Instance.CountItem(item);
            int required = entry.GetMax();
            var inventory = Player.m_localPlayer.GetInventory();
            int carried = SalvageJunkProtection.IsJunk(element.GetComponentInParent<ConvertUI>())
                ? SalvageJunkProtection.Count(inventory, item.m_shared.m_name)
                : inventory.CountItems(item.m_shared.m_name);
            string quantity = $"{ShortCount(available)} / {ShortCount(required)}";
            element.ItemTotalQuantity.text = available < required ? $"<color=red>{quantity}</color>" : quantity;

            // The exact counts, and where they come from only when nearby storage adds to the bag.
            string summary = available >= 10000 || required >= 10000 ? $"\n\nAvailable / required: {available} / {required}" : "";
            if (available > carried) summary += (summary.Length == 0 ? "\n\n" : "\n") +
                $"Carried / nearby storage: {carried} / {available - carried}";
            UpdateTooltip(element.Tooltip, summary);
            if (element.ItemHoverTooltip != element.Tooltip) UpdateTooltip(element.ItemHoverTooltip, summary);
            lastSummary = summary;
        }

        // From 10,000 up, show whole thousands rounded down ("12480" as "12k") so large nearby-storage totals stay
        // readable instead of shrinking the label. The red color uses the exact counts, and the tooltip keeps them.
        private static string ShortCount(int count) => count < 10000 ? count.ToString() : count / 1000 + "k";

        private void UpdateTooltip(UITooltip tooltip, string summary)
        {
            if (tooltip == null) return;
            string original = tooltip.m_text ?? "";
            if (lastSummary.Length > 0 && original.EndsWith(lastSummary, StringComparison.Ordinal))
                original = original.Substring(0, original.Length - lastSummary.Length);
            tooltip.Set(tooltip.m_topic, original + summary);
        }
    }

    // List refreshes keep the mouse scroll position; active controller navigation still centers its focused row.
    [HarmonyPatch(typeof(MultiSelectItemList), "CenterOnItem")]
    internal static class EnchantingListScroll
    {
        private static bool Prefix() => ZInput.IsGamepadActive();
    }

    // The table's selectable lists fill each row's tooltip here, and EpicLoot left it empty for anything but equippable
    // gear, so shardstones, trophies and boss items in Sacrifice showed no tooltip at all. Give those rows the item's
    // inventory tooltip, titled as EpicLoot titles gear.
    [HarmonyPatch(typeof(EnchantingUIController), "SetItemTooltip")]
    internal static class EnchantingListTooltips
    {
        private static bool Prefix(ItemDrop.ItemData item, UITooltip tooltip)
        {
            if (EpicLoot.EpicLoot.IsAllowedMagicItemType(item)) return true;
            tooltip.Set(item.GetDisplayName(), item.GetTooltip());
            return false;
        }
    }

    // EpicLoot's result window after an enchant or a rune etch closed on Escape, Tab or gamepad B by itself while the
    // enchanting table, reading the same key in the same frame, closed too, depending on which ran first. That key now
    // closes only the window, whichever runs first, and the next press closes the table. A click outside the window
    // closes it too, without reaching the table behind it.
    [HarmonyPatch]
    internal static class EnchantingResultDialog
    {
        private const string OutsideName = "ProgressionFixOutsideClick";
        private static int closedFrame = -1;

        private static bool OnTable(CraftSuccessDialog dialog) =>
            EnchantingTableUI.instance != null && dialog.transform.parent == EnchantingTableUI.instance.transform;

        [HarmonyPatch(typeof(CraftSuccessDialog), nameof(CraftSuccessDialog.Close)), HarmonyPostfix]
        private static void Closed(CraftSuccessDialog __instance)
        {
            if (OnTable(__instance)) closedFrame = Time.frameCount;
        }

        [HarmonyPatch(typeof(EnchantingTableUI), nameof(EnchantingTableUI.Update)), HarmonyPrefix]
        private static bool CloseWindowFirst(EnchantingTableUI __instance)
        {
            if (closedFrame == Time.frameCount) return false;
            if (__instance.Root == null || !__instance.Root.activeSelf ||
                !(ZInput.GetButtonDown("JoyButtonB") || ZInput.GetKeyDown(KeyCode.Escape) || ZInput.GetKeyDown(KeyCode.Tab))) return true;
            foreach (var dialog in __instance.GetComponentsInChildren<CraftSuccessDialog>())
                if (OnTable(dialog))
                {
                    dialog.Close();
                    return false;
                }
            return true;
        }

        // A transparent layer under the window, centered on it and larger than any screen, takes the clicks outside it.
        [HarmonyPatch(typeof(CraftSuccessDialog), nameof(CraftSuccessDialog.Show)), HarmonyPostfix]
        private static void CloseOnOutsideClick(CraftSuccessDialog __instance)
        {
            if (!OnTable(__instance) || __instance.transform.Find(OutsideName) != null) return;
            var outside = new GameObject(OutsideName, typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)outside.transform;
            rect.SetParent(__instance.transform, false);
            rect.SetAsFirstSibling();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(20000f, 20000f);
            outside.GetComponent<Image>().color = Color.clear;
            var button = outside.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(__instance.Close);
        }
    }

    // EpicLoot reads Shift only while it builds a tooltip's text. The inventory rebuilds the hovered slot's tooltip every
    // frame, but the enchanting table builds each row's tooltips once, when its list is filled, so pressing Shift over a
    // row changed nothing. Refresh the hovered row through the same setters, including health-dependent costs.
    [HarmonyPatch(typeof(MultiSelectItemListElement), nameof(MultiSelectItemListElement.SetItem))]
    public sealed class EnchantingTooltipDetails : MonoBehaviour
    {
        private static readonly AccessTools.FieldRef<UITooltip> ShownTooltip =
            AccessTools.StaticFieldRefAccess<UITooltip>(AccessTools.Field(typeof(UITooltip), "m_current"));
        private static readonly Action<UITooltip> ShowNewText =
            AccessTools.MethodDelegate<Action<UITooltip>>(AccessTools.Method(typeof(UITooltip), "UpdateTextElements"));
        private static readonly Action<ItemDrop.ItemData, UITooltip> SetItemTooltip = AccessTools.MethodDelegate<Action<ItemDrop.ItemData, UITooltip>>(
            AccessTools.Method(typeof(EnchantingUIController), "SetItemTooltip", new[] { typeof(ItemDrop.ItemData), typeof(UITooltip) }));

        private MultiSelectItemListElement element;
        private bool builtWithDetails;
        private float refreshAt;

        private static void Postfix(MultiSelectItemListElement __instance)
        {
            var tracker = __instance.GetComponent<EnchantingTooltipDetails>();
            if (tracker == null) tracker = __instance.gameObject.AddComponent<EnchantingTooltipDetails>();
            tracker.element = __instance;
            tracker.builtWithDetails = MagicItem.ShowEffectDetails;
        }

        private void Update()
        {
            var shown = ShownTooltip();
            if (shown == null || element == null || (shown != element.Tooltip && shown != element.ItemHoverTooltip)) return;
            bool details = MagicItem.ShowEffectDetails;
            if (details == builtWithDetails && Time.unscaledTime < refreshAt) return;
            builtWithDetails = details;
            refreshAt = Time.unscaledTime + .2f;
            var item = element.GetListElement()?.GetItem();
            if (item == null) return;
            // Same order and text as SetItem: SetMagicItem writes Tooltip's fields directly, without Auga, and
            // SetItemTooltip sets ItemHoverTooltip, which also refreshes it on screen.
            if (element.Tooltip != null && !EpicLoot.EpicLoot.HasAuga)
            {
                element.Tooltip.m_topic = Localization.instance.Localize(item.GetDecoratedName());
                element.Tooltip.m_text = Localization.instance.Localize(item.GetTooltip());
                if (shown == element.Tooltip) ShowNewText(shown);
            }
            if (element.ItemHoverTooltip != null) SetItemTooltip(item, element.ItemHoverTooltip);
            // Cost rows append a stock summary that the rebuilt text lacks.
            var costs = GetComponent<EnchantingCostCounts>();
            if (costs != null) costs.RefreshCount();
        }
    }

    // EpicLoot scrolls an item tooltip taller than its Tooltip Max Height with the mouse wheel wherever the pointer is,
    // and the same wheel scrolled the enchanting-table list under the pointer. The list moved the hovered row away from
    // the pointer, which closes the tooltip, so a long tooltip, such as one expanded with Shift, could not be scrolled.
    // While such a tooltip is shown, the wheel leaves the table's lists alone.
    [HarmonyPatch(typeof(ScrollRect), nameof(ScrollRect.OnScroll))]
    internal static class EnchantingTooltipScroll
    {
        private static readonly AccessTools.FieldRef<GameObject> ShownTooltip =
            AccessTools.StaticFieldRefAccess<GameObject>(AccessTools.Field(typeof(UITooltip), "m_tooltip"));

        private static bool Prefix(ScrollRect __instance)
        {
            var table = EnchantingTableUI.instance;
            var tooltip = ShownTooltip();
            if (table == null || tooltip == null || !tooltip.activeInHierarchy || !__instance.transform.IsChildOf(table.transform))
                return true;
            // EpicLoot's scroll view inside the tooltip; the tooltip scrolls only when its content is taller than the view.
            var wheel = tooltip.GetComponentInChildren<ScrollWheelHandler>();
            var view = wheel != null ? wheel.GetComponent<ScrollRect>() : null;
            return view == null || view.content.rect.height <= view.viewport.rect.height;
        }
    }
}
