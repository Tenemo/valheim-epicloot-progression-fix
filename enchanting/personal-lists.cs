using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpicLoot.CraftingV2;
using EpicLoot_UnityLib;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Like the sacrifice list, the Augment, Enchant and rune etching item lists and the runes offered for etching hold
    // only the player's own inventory, equipped and quick-slot items included, instead of also offering items from nearby
    // chests. Their costs keep their providers, so nearby chests can still supply the ingredients. EpicLoot asks for the
    // etching rune list again when the countdown ends, so a rune that left the inventory cancels the etch.
    [HarmonyPatch]
    internal static class PersonalItemLists
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            new[] { "GetAugmentableItems", "GetEnchantableItems", "GetRuneEtchItems", "GetApplyableRunesforItem",
                "GetRuneSetEtchItems", "GetApplyableSetRunesForItem" }
                .Select(name => AccessTools.Method(typeof(EnchantingUIController), name)
                    ?? throw new MissingMethodException(nameof(EnchantingUIController), name));

        private static void Postfix(List<InventoryItemListElement> __result) =>
            __result?.RemoveAll(entry => entry == null || !FavoriteProtection.InInventory(entry.GetItem()));
    }

    // Filter the item being stripped; ingredient costs keep their nearby-chest providers.
    [HarmonyPatch(typeof(EnchantingUIController), "GetDisenchantItems")]
    internal static class PersonalDisenchantList
    {
        private static void Postfix(List<InventoryItemListElement> __result)
        {
            if (!Plugin.Ready) { __result?.Clear(); return; }
            __result?.RemoveAll(entry => entry == null || !FavoriteProtection.CanSacrifice(entry.GetItem()));
        }
    }

    [HarmonyPatch(typeof(DisenchantUI), "DoMainAction")]
    internal static class RecheckDisenchantSelection
    {
        private static readonly Action<DisenchantUI> Refresh = AccessTools.MethodDelegate<Action<DisenchantUI>>(
            AccessTools.Method(typeof(DisenchantUI), "RefreshAvailableItems"));

        private static bool Prefix(DisenchantUI __instance)
        {
            var item = __instance.AvailableItems.GetSingleSelectedItem<InventoryItemListElement>()?.Item1.GetItem();
            if (Plugin.Ready && FavoriteProtection.CanSacrifice(item)) return true;

            FavoriteProtection.CancelSelection(__instance, () => Refresh(__instance), Plugin.Ready
                ? "Disenchanting cancelled: the item is no longer in your inventory, or it is favorited."
                : "Disenchanting blocked: EpicLoot Progression Fix did not initialize. Fix the mod installation first.");
            return false;
        }
    }

    // Enchanting changes carried equipment, including favorites, equipped items and quick slots.
    [HarmonyPatch(typeof(EnchantUI), "DoMainAction")]
    internal static class RecheckEnchantSelection
    {
        private static readonly Action<EnchantUI> Refresh = AccessTools.MethodDelegate<Action<EnchantUI>>(
            AccessTools.Method(typeof(EnchantUI), "RefreshAvailableItems"));

        private static bool Prefix(EnchantUI __instance)
        {
            var item = __instance.AvailableItems.GetSelectedItems<InventoryItemListElement>().FirstOrDefault()?.Item1.GetItem();
            if (item == null || FavoriteProtection.InInventory(item)) return true;
            FavoriteProtection.CancelSelection(__instance, () => Refresh(__instance),
                "Enchanting cancelled: the item is no longer in your inventory.");
            return false;
        }
    }

    internal static class PersonalRunes
    {
        // Extraction takes the enchantment off its item, so, like Sacrifice, it also leaves out favorites, and offers
        // nothing while the add-on cannot read them. Etching takes favorited items and runes.
        internal static bool CanExtract(ItemDrop.ItemData item) =>
            Plugin.Ready && FavoriteProtection.CanSacrifice(item);
    }

    [HarmonyPatch]
    internal static class PersonalExtractItemList
    {
        private static IEnumerable<MethodBase> TargetMethods() => new[] { "GetRuneExtractItems", "GetRuneSetExtractItems" }
            .Select(name => AccessTools.Method(typeof(EnchantingUIController), name));
        private static void Postfix(List<InventoryItemListElement> __result) =>
            __result?.RemoveAll(entry => entry == null || !PersonalRunes.CanExtract(entry.GetItem()));
    }

    // Completion checks include nearby chests. Targets must still be carried, and extraction also rechecks favorites,
    // before anything is paid or changed. Etching preserves its existing permission to edit favorite targets.
    [HarmonyPatch]
    internal static class RecheckRuneSelection
    {
        private static IEnumerable<MethodBase> TargetMethods() => new[] { "ExtractSelectedEnchantment", "ExtractSelectedSet", "EtchSelectedRune", "EtchSelectedSetRune" }
            .Select(name => AccessTools.Method(typeof(RuneUI), name));
        private static readonly Action<RuneUI, string, bool, ItemDrop.ItemData> Abort = AccessTools.MethodDelegate<Action<RuneUI, string, bool, ItemDrop.ItemData>>(
            AccessTools.Method(typeof(RuneUI), "AbortMainAction"));

        private static bool Prefix(RuneUI __instance, MethodBase __originalMethod, ItemDrop.ItemData item, ref bool __result)
        {
            bool extraction = __originalMethod.Name.StartsWith("Extract", StringComparison.Ordinal);
            if (extraction ? PersonalRunes.CanExtract(item) : Plugin.Ready && FavoriteProtection.InInventory(item)) return true;
            Abort(__instance, "the item left the inventory or is protected", false, item);
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                "Rune action cancelled: the item is no longer in your inventory, or extraction is protected.");
            __result = false;
            return false;
        }
    }
}
