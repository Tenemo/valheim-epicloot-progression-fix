using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using EpicLoot.CraftingV2;
using EpicLoot_UnityLib;
using HarmonyLib;
using TMPro;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(StashCatalog), nameof(StashCatalog.IsStashable))]
    internal static class StashFavoriteProtection
    {
        private static void Postfix(ItemDrop.ItemData item, ref bool __result)
        {
            if (__result && FavoriteProtection.InInventory(item))
                __result = Plugin.Ready && !FavoriteProtection.IsFavorite(Player.m_localPlayer, item);
        }
    }

    internal static class FavoriteProtection
    {
        private static MethodInfo getPlayerConfig;
        private static MethodInfo favorite;
        private static bool quickStackLoaded;
        private static bool reportedFailure;
        private static ManualLogSource logger;

        internal static void Initialize(ManualLogSource log)
        {
            logger = log;
            try
            {
                quickStackLoaded = PluginIds.Installed(PluginIds.QuickStack);
                if (!quickStackLoaded) return;
                if (!PluginIds.TryGet(PluginIds.QuickStack, out var plugin))
                {
                    ReportFavoriteFailure(PluginIds.UnavailableReason(PluginIds.QuickStack));
                    return;
                }
                var type = plugin.Instance.GetType().Assembly.GetType("QuickStackStore.UserConfig");
                if (type != null)
                {
                    getPlayerConfig = AccessTools.Method(type, "GetPlayerConfig", new[] { typeof(long) });
                    favorite = AccessTools.Method(type, "IsItemNameOrSlotFavorited", new[] { typeof(ItemDrop.ItemData) });
                }
                if (getPlayerConfig == null || favorite == null) ReportFavoriteFailure("Quick Stack's favorite API was not found.");
            }
            catch (InvalidOperationException error)
            {
                // Another Quick Stack version leaves favorites unverifiable, which blocks these actions rather than
                // leaving the protection unpatched.
                quickStackLoaded = true;
                ReportFavoriteFailure(error.Message);
            }
        }

        private static void ReportFavoriteFailure(string reason)
        {
            if (reportedFailure) return;
            reportedFailure = true;
            logger.LogWarning("Cannot verify favorites; Sacrifice, Disenchant, rune extraction and Salvage junk are blocked to protect inventory. " + reason);
        }

        internal static bool IsFavorite(Player player, ItemDrop.ItemData item)
        {
            if (!quickStackLoaded) return false;
            if (getPlayerConfig == null || favorite == null || !OptionalIntegrations.Enabled(typeof(PersistentFavorites))) return true;
            try
            {
                var playerConfig = getPlayerConfig.Invoke(null, new object[] { player.GetPlayerID() });
                return (bool)favorite.Invoke(playerConfig, new object[] { item });
            }
            catch (Exception error)
            {
                ReportFavoriteFailure(error.GetBaseException().Message);
                return true;
            }
        }

        // Container cells do not inherit the player's protected inventory-slot coordinates.
        internal static bool IsFavoriteInStorage(ItemDrop.ItemData item) => quickStackLoaded &&
            (getPlayerConfig == null || favorite == null || !OptionalIntegrations.Enabled(typeof(PersistentFavorites)) || PersistentFavorites.IsFavoriteItem(item));

        // The local player's own inventory, equipped and quick-slot items included, without nearby chests.
        internal static bool InInventory(ItemDrop.ItemData item) =>
            item != null && Player.m_localPlayer != null && Player.m_localPlayer.GetInventory().ContainsItem(item);

        internal static bool CanSacrifice(ItemDrop.ItemData item) => InInventory(item) && !IsFavorite(Player.m_localPlayer, item);

        // Cancels a table action whose selection is no longer allowed, shows the refreshed list with nothing selected,
        // and tells the player why.
        internal static void CancelSelection(EnchantingTableUIPanelBase panel, Action refresh, string message)
        {
            panel.Cancel();
            refresh();
            panel.AvailableItems.DeselectAll();
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, message);
        }

        private static MethodInfo available;
        private static object[] allCategories;
        // Every category except the table's own Storage, whose items this pack never sacrifices anyway.
        internal static List<InventoryItemListElement> Available()
        {
            if (available == null)
            {
                available = AccessTools.Method(typeof(EnchantingUIController), "GetSacrificeItems");
                allCategories = new[] { Enum.Parse(available.GetParameters()[0].ParameterType, "All") };
            }
            return (List<InventoryItemListElement>)available.Invoke(null, allCategories);
        }
    }

    // Scope this to the sacrifice target list. Other tabs and resource counting keep their providers.
    [HarmonyPatch(typeof(EnchantingUIController), "GetSacrificeItems")]
    internal static class PersonalSacrificeList
    {
        private static void Postfix(List<InventoryItemListElement> __result)
        {
            if (!Plugin.Ready) { __result?.Clear(); return; }
            __result?.RemoveAll(entry => entry == null || !FavoriteProtection.CanSacrifice(entry.GetItem()));
        }
    }

    // Sacrifice takes only carried items, so the table Storage category would always be empty. It is the last option,
    // and the selected index maps straight to EpicLoot's category list.
    [HarmonyPatch(typeof(SacrificeUI), "CreateSacrificeCategoryPanel")]
    internal static class CarriedSacrificeCategories
    {
        private static readonly Array Categories = (Array)AccessTools.Field(typeof(EnchantingUIController), "SacrificeCategoryOptions").GetValue(null);

        private static void Postfix(TMP_Dropdown ____sacrificeCategory)
        {
            int storage = Categories.Length - 1;
            if (____sacrificeCategory == null || ____sacrificeCategory.options.Count != Categories.Length ||
                Categories.GetValue(storage).ToString() != "TableStorage") return;
            ____sacrificeCategory.options.RemoveAt(storage);
            ____sacrificeCategory.RefreshShownValue();
        }
    }

    [HarmonyPatch(typeof(SacrificeUI), "SacrificeItems")]
    internal static class RecheckSacrificeSelection
    {
        private static readonly Action<SacrificeUI> Refresh = AccessTools.MethodDelegate<Action<SacrificeUI>>(
            AccessTools.Method(typeof(SacrificeUI), "RefreshAvailableItems"));

        private static bool Prefix(SacrificeUI __instance)
        {
            if (!Plugin.Ready)
            {
                __instance.Cancel();
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Sacrifice blocked: EpicLoot Progression Fix did not initialize. Fix the mod installation first.");
                return false;
            }
            var selected = __instance.AvailableItems.GetSelectedItems<IListElement>().Where(x => x.Item2 > 0).ToList();
            if (selected.Count == 0) return false;
            // Rebuild eligibility after the countdown, including EpicLoot's equipped/hotbar and provider filters.
            var allowed = new HashSet<ItemDrop.ItemData>(FavoriteProtection.Available().Select(x => x.GetItem()));
            if (selected.All(x => FavoriteProtection.CanSacrifice(x.Item1.GetItem()) && allowed.Contains(x.Item1.GetItem()) &&
                x.Item2 <= x.Item1.GetItem().m_stack)) return true;

            FavoriteProtection.CancelSelection(__instance, () => Refresh(__instance),
                "Sacrifice cancelled: an item is no longer eligible.");
            return false;
        }
    }
}
