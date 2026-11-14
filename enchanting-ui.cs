using System;
using System.Collections.Generic;
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
        private bool costRow;
        private float nextRefresh;
        private string lastSummary = "";

        private static void Postfix(MultiSelectItemListElement __instance)
        {
            if (__instance.GetListElement()?.GetItem() == null) return;
            var display = __instance.GetComponent<EnchantingCostCounts>();
            if (display == null)
            {
                // Cost rows check affordability and get the count on the tile. Convert Materials' recipe and product
                // rows show what the item is, not how many you own, so their tooltips get that count instead.
                bool cost = __instance.ReadOnly && __instance.CheckPlayerInventory && __instance.ItemTotalQuantity != null;
                if (!cost && __instance.GetComponentInParent<ConvertUI>() == null) return;
                display = __instance.gameObject.AddComponent<EnchantingCostCounts>();
                display.element = __instance;
                display.costRow = cost;
                if (cost)
                {
                    // Show the count at 1.5 times the prefab's base size, shrinking back toward it only when a long
                    // count would not fit the tile on one line.
                    var text = __instance.ItemTotalQuantity;
                    text.fontSizeMax = Mathf.RoundToInt(text.fontSize * 1.5f);
                    text.fontSizeMin = Mathf.Min(8f, text.fontSize);
                    text.enableAutoSizing = true;
                    // EpicLoot draws the count in a plain white font that vanishes on bright item pictures. Use the
                    // inventory's count font and its dark outline instead, at the same size.
                    var inventoryCount = RuneIconLabels.FontTemplate;
                    text.font = inventoryCount.font;
                    text.fontSharedMaterial = inventoryCount.fontSharedMaterial;
                }
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
            // Every half second on a grid of its own, so a filled list's rows don't all count nearby chests in one frame.
            float phase = (GetInstanceID() & 15) / 32f;
            nextRefresh = (Mathf.Floor((Time.unscaledTime - phase) / 0.5f) + 1f) * 0.5f + phase;
            if (element == null || Player.m_localPlayer == null) return;
            var entry = element.GetListElement();
            var item = entry?.GetItem();
            if (item == null) return;

            // Salvage junk counts no favorites, carried or stored, the same as its provider count below.
            var inventory = Player.m_localPlayer.GetInventory();
            int carried = SalvageJunkProtection.IsJunk(element.GetComponentInParent<ConvertUI>())
                ? SalvageJunkProtection.Count(inventory, item.m_shared.m_name)
                : inventory.CountItems(item.m_shared.m_name);
            if (!costRow)
            {
                // What nearby storage holds, also of the shardstones that Upgrade shardstones takes only from the bag.
                int stored;
                ShardstoneUpgradeInventory.CountingStorage = true;
                try { stored = InventoryManagement.Instance.CountItem(item) - carried; }
                finally { ShardstoneUpgradeInventory.CountingStorage = false; }
                ShowSummary($"\n\nCarried / nearby storage: {carried} / {stored}");
                return;
            }

            // Use the same provider count as HasItem, including CraftyBoxes' range and pull filters.
            int available = InventoryManagement.Instance.CountItem(item);
            int required = entry.GetMax();
            string quantity = $"{ShortCount(available)} / {ShortCount(required)}";
            element.ItemTotalQuantity.text = available < required ? $"<color=red>{quantity}</color>" : quantity;

            // The exact counts, and where they come from only when nearby storage adds to the bag.
            string summary = available >= 10000 || required >= 10000 ? $"\n\nAvailable / required: {available} / {required}" : "";
            if (available > carried) summary += (summary.Length == 0 ? "\n\n" : "\n") +
                $"Carried / nearby storage: {carried} / {available - carried}";
            ShowSummary(summary);
        }

        private void ShowSummary(string summary)
        {
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
            if (EnchantingTooltipDetails.Deferring)
            {
                tooltip.m_topic = tooltip.m_text = "";
                return false;
            }
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

    // EpicLoot builds every row's item tooltip, twice, while it fills a list, which made opening the table and switching
    // its tabs stall with a full bag or nearby chests. The rows you pick items from build their tooltip once, when it is
    // about to show, and meanwhile a millisecond's worth of rows a frame from the top, so a hovered row is normally ready.
    // EpicLoot also reads Shift only while it builds a tooltip's text, so pressing Shift over a row changed nothing; the
    // hovered row rebuilds when Shift changes and every 0.2 s, for health-dependent costs.
    [HarmonyPatch(typeof(MultiSelectItemListElement), nameof(MultiSelectItemListElement.SetItem))]
    public sealed class EnchantingTooltipDetails : MonoBehaviour
    {
        private const double FrameBudgetMilliseconds = 1;

        private static readonly AccessTools.FieldRef<UITooltip> ShownTooltip =
            AccessTools.StaticFieldRefAccess<UITooltip>(AccessTools.Field(typeof(UITooltip), "m_current"));
        private static readonly Action<UITooltip> ShowNewText =
            AccessTools.MethodDelegate<Action<UITooltip>>(AccessTools.Method(typeof(UITooltip), "UpdateTextElements"));
        private static readonly Action<ItemDrop.ItemData, UITooltip> SetItemTooltip = AccessTools.MethodDelegate<Action<ItemDrop.ItemData, UITooltip>>(
            AccessTools.Method(typeof(EnchantingUIController), "SetItemTooltip", new[] { typeof(ItemDrop.ItemData), typeof(UITooltip) }));
        private static readonly Func<MultiSelectItemListElement, int> StorageCount = AccessTools.MethodDelegate<Func<MultiSelectItemListElement, int>>(
            AccessTools.Method(typeof(MultiSelectItemListElement), "GetStorageCount"));
        private static readonly Queue<EnchantingTooltipDetails> Pending = new Queue<EnchantingTooltipDetails>();
        private static readonly System.Diagnostics.Stopwatch BudgetClock = new System.Diagnostics.Stopwatch();

        // True while SetItem fills a row whose tooltip waits; SetMagicItem and SetItemTooltip then leave it empty.
        internal static bool Deferring { get; private set; }

        private MultiSelectItemListElement element;
        private bool waiting;
        private bool queued;
        private bool builtWithDetails;
        private float refreshAt;

        private static void Prefix(MultiSelectItemListElement __instance, IListElement item) => Deferring = CanWait(__instance, item);

        private static void Postfix(MultiSelectItemListElement __instance)
        {
            bool deferred = Deferring;
            Deferring = false;
            var tracker = __instance.GetComponent<EnchantingTooltipDetails>();
            if (tracker == null) tracker = __instance.gameObject.AddComponent<EnchantingTooltipDetails>();
            tracker.element = __instance;
            tracker.builtWithDetails = MagicItem.ShowEffectDetails;
            tracker.refreshAt = Time.unscaledTime + .2f;
            tracker.waiting = deferred;
            if (deferred && !tracker.queued)
            {
                tracker.queued = true;
                Pending.Enqueue(tracker);
            }
        }

        private static void Finalizer() => Deferring = false;

        // Cost, product and Convert Materials rows are few and keep EpicLoot's own filling, as do Auga's tooltips. A row
        // whose tooltip is showing or under the pointer fills at once, as EpicLoot's own setter shows the new text there.
        private static bool CanWait(MultiSelectItemListElement element, IListElement item)
        {
            if (element.ReadOnly || EpicLoot.EpicLoot.HasAuga || item?.GetItem() == null) return false;
            var shown = ShownTooltip();
            if (shown != null && (shown == element.Tooltip || shown == element.ItemHoverTooltip)) return false;
            if (ZInput.instance != null && element.transform is RectTransform rect &&
                rect.rect.Contains(rect.InverseTransformPoint(ZInput.pointerPosition))) return false;
            return element.GetComponentInParent<ConvertUI>() == null;
        }

        // Same text and order as SetItem with the pack's tooltips: SetMagicItem writes Tooltip's fields directly, SetItem
        // adds the stored count, and SetItemTooltip sets ItemHoverTooltip, which also refreshes it on screen. Without
        // Auga both use the same tooltip text, so it is built once.
        internal void Build()
        {
            waiting = false;
            builtWithDetails = MagicItem.ShowEffectDetails;
            refreshAt = Time.unscaledTime + .2f;
            var item = element == null ? null : element.GetListElement()?.GetItem();
            if (item == null) return;
            if (EpicLoot.EpicLoot.HasAuga)
            {
                if (element.ItemHoverTooltip != null) SetItemTooltip(item, element.ItemHoverTooltip);
            }
            else
            {
                string text = item.GetTooltip();
                if (element.Tooltip != null)
                {
                    element.Tooltip.m_topic = Localization.instance.Localize(item.GetDecoratedName());
                    element.Tooltip.m_text = Localization.instance.Localize(text);
                    int stored = StorageCount(element);
                    if (stored > 0) element.Tooltip.m_text += "\n<color=#9BDB9B>" +
                        Localization.instance.Localize("$mod_epicloot_storagecount", stored.ToString()) + "</color>";
                    if (ShownTooltip() == element.Tooltip) ShowNewText(element.Tooltip);
                }
                if (element.ItemHoverTooltip != null) element.ItemHoverTooltip.Set(item.GetDisplayName(), text);
            }
            // Cost rows append a stock summary that the rebuilt text lacks.
            var costs = GetComponent<EnchantingCostCounts>();
            if (costs != null) costs.RefreshCount();
        }

        private void Update()
        {
            var shown = ShownTooltip();
            if (shown == null || element == null || (shown != element.Tooltip && shown != element.ItemHoverTooltip)) return;
            if (!waiting && MagicItem.ShowEffectDetails == builtWithDetails && Time.unscaledTime < refreshAt) return;
            Build();
        }

        // Before a waiting row's tooltip decides whether it has anything to show, by mouse, touch or controller focus.
        internal static void BuildOnHover(UITooltip tooltip)
        {
            var tracker = tooltip.GetComponentInParent<EnchantingTooltipDetails>();
            if (tracker == null || tracker.element == null ||
                (tooltip != tracker.element.Tooltip && tooltip != tracker.element.ItemHoverTooltip)) return;
            if (tracker.waiting || tracker.builtWithDetails != MagicItem.ShowEffectDetails) tracker.Build();
        }

        // Rows build in list order while the table is open, at least one a frame. Rows on a hidden tab or filtered out
        // wait for their hover; switching tabs fills that tab's list again.
        internal static void BuildWaiting(EnchantingTableUI table)
        {
            if (Pending.Count == 0 || table.Root == null || !table.Root.activeSelf) return;
            BudgetClock.Restart();
            while (Pending.Count > 0)
            {
                var tracker = Pending.Dequeue();
                if (tracker == null) continue;
                tracker.queued = false;
                if (!tracker.waiting || !tracker.gameObject.activeInHierarchy) continue;
                tracker.Build();
                if (BudgetClock.Elapsed.TotalMilliseconds >= FrameBudgetMilliseconds) break;
            }
        }
    }

    [HarmonyPatch]
    internal static class EnchantingTooltipBuilding
    {
        [HarmonyPatch(typeof(UITooltip), nameof(UITooltip.OnHoverStart)), HarmonyPrefix]
        private static void Hovered(UITooltip __instance) => EnchantingTooltipDetails.BuildOnHover(__instance);

        [HarmonyPatch(typeof(EnchantingTableUI), nameof(EnchantingTableUI.Update)), HarmonyPostfix]
        private static void TableUpdated(EnchantingTableUI __instance) => EnchantingTooltipDetails.BuildWaiting(__instance);

        // A waiting row leaves Tooltip empty here; the row builds it later, with the same text.
        [HarmonyPatch(typeof(EnchantingUIController), "SetMagicItem"), HarmonyPrefix]
        private static void Filling(ref UITooltip tooltip)
        {
            if (!EnchantingTooltipDetails.Deferring || tooltip == null) return;
            tooltip.m_topic = tooltip.m_text = "";
            tooltip = null;
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
