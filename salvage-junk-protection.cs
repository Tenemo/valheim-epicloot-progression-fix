using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpicLoot.CraftingV2;
using EpicLoot_UnityLib;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class SalvageJunkProtection
    {
        internal static bool ListingJunk, ConvertingJunk, FilteringInventory;
        internal static readonly Action<Inventory, bool, bool> Changed =
            AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(
                AccessTools.Method(typeof(Inventory), "Changed", new[] { typeof(bool), typeof(bool) }));

        internal static bool IsJunk(ConvertUI panel) => panel != null && GameFields.ConversionMode(panel) == MaterialConversionType.Junk;

        internal static bool ShowingJunk()
        {
            var table = EnchantingTableUI.instance;
            if (table == null || !table.gameObject.activeInHierarchy) return false;
            foreach (var panel in table.Panels)
                if (panel is ConvertUI convert && convert.isActiveAndEnabled) return IsJunk(convert);
            return false;
        }

        internal static bool CanUse(Inventory inventory, ItemDrop.ItemData item, string name, int quality, bool matchWorldLevel)
        {
            var player = Player.m_localPlayer;
            if (!Plugin.Ready || player == null || (name != null && item.m_shared.m_name != name) ||
                (quality >= 0 && item.m_quality != quality) || (matchWorldLevel && item.m_worldLevel < Game.m_worldLevel)) return false;
            return ReferenceEquals(inventory, player.GetInventory())
                ? !FavoriteProtection.IsFavorite(player, item)
                : !FavoriteProtection.IsFavoriteInStorage(item);
        }

        internal static int Count(Inventory inventory, string name, int quality = -1, bool matchWorldLevel = true)
        {
            int count = 0;
            foreach (var item in inventory.GetAllItems())
                if (CanUse(inventory, item, name, quality, matchWorldLevel)) count += item.m_stack;
            return count;
        }
    }

    [HarmonyPatch(typeof(EnchantingUIController), "GetConversionRecipes")]
    internal static class SalvageJunkRecipeCounts
    {
        private static void Prefix(MaterialConversionType __0, out bool __state)
        {
            __state = SalvageJunkProtection.ListingJunk;
            SalvageJunkProtection.ListingJunk = __0 == MaterialConversionType.Junk;
        }

        private static void Finalizer(bool __state) => SalvageJunkProtection.ListingJunk = __state;
    }

    // Filter the native inventory calls inside EpicLoot's providers, retaining their chest access, pulling and
    // leave-one rules. Direct game inventory queries and the other enchanting tabs keep their normal counts.
    [HarmonyPatch]
    internal static class SalvageJunkMaterialScope
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(InventoryManagement), "CountItem", new[] { typeof(string) });
            yield return AccessTools.Method(typeof(InventoryManagement), "RemoveItem", new[] { typeof(string), typeof(int) });
        }

        private static void Prefix(MethodBase __originalMethod, out bool __state)
        {
            __state = SalvageJunkProtection.FilteringInventory;
            if (SalvageJunkProtection.ConvertingJunk || (__originalMethod.Name == "CountItem" &&
                (SalvageJunkProtection.ListingJunk || SalvageJunkProtection.ShowingJunk())))
                SalvageJunkProtection.FilteringInventory = true;
        }

        private static void Finalizer(bool __state) => SalvageJunkProtection.FilteringInventory = __state;
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItems), new[] { typeof(string), typeof(int), typeof(bool) })]
    internal static class SalvageJunkInventoryCounts
    {
        private static bool Prefix(Inventory __instance, string __0, int __1, bool __2, ref int __result)
        {
            if (!SalvageJunkProtection.FilteringInventory) return true;
            __result = SalvageJunkProtection.Count(__instance, __0, __1, __2);
            return false;
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(string), typeof(int), typeof(int), typeof(bool) })]
    internal static class SalvageJunkInventoryPayment
    {
        private static bool Prefix(Inventory __instance, string __0, int __1, int __2, bool __3)
        {
            if (!SalvageJunkProtection.FilteringInventory || !SalvageJunkProtection.ConvertingJunk) return true;
            // Keep native stack order and one Changed notification, consuming only the actual eligible stacks.
            foreach (var item in __instance.GetAllItems())
            {
                if (!SalvageJunkProtection.CanUse(__instance, item, __0, __2, __3)) continue;
                int amount = Math.Min(item.m_stack, __1);
                item.m_stack -= amount;
                __1 -= amount;
                if (__1 <= 0) break;
            }
            __instance.GetAllItems().RemoveAll(item => item.m_stack <= 0);
            SalvageJunkProtection.Changed(__instance, false, false);
            return false;
        }
    }

    [HarmonyPatch(typeof(ConvertUI), "DoMainAction")]
    internal static class RecheckSalvageJunk
    {
        private static bool Prefix(ConvertUI __instance, out bool __state)
        {
            __state = SalvageJunkProtection.ConvertingJunk;
            if (!SalvageJunkProtection.IsJunk(__instance)) return true;
            SalvageJunkProtection.ConvertingJunk = true;
            var costs = ConvertUI.GetConversionCost(__instance.AvailableItems.GetSelectedItems<ConversionRecipeUnity>());
            if (Plugin.Ready && (Player.m_localPlayer.NoCostCheat() ||
                costs.All(cost => InventoryManagement.Instance.HasItem(cost.GetItem())))) return true;

            FavoriteProtection.CancelSelection(__instance, __instance.RefreshAvailableItems, Plugin.Ready
                ? "Salvage cancelled: not enough unprotected materials remain."
                : "Salvage blocked: EpicLoot Progression Fix did not initialize. Fix the mod installation first.");
            return false;
        }

        private static void Finalizer(bool __state) => SalvageJunkProtection.ConvertingJunk = __state;
    }
}
