using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Keep the native, once-per-character purchases, change only when Haldor offers them, and add a third after Yagluth.
    [HarmonyPatch(typeof(Trader), nameof(Trader.GetAvailableItems))]
    internal static class InventoryUpgradeProgression
    {
        internal const string ThirdPurchaseKey = Plugin.Id + ".invslot3";

        private static void Prefix(Trader __instance)
        {
            if (__instance.m_name != "$npc_haldor") return;
            Trader.TradeItem second = null;
            bool third = false;
            foreach (var item in __instance.m_items)
            {
                if (item.m_incrementKey != "invrows") continue;
                if (item.m_buyKey == "invslot1") item.m_requiredGlobalKey = "defeated_gdking";
                else if (item.m_buyKey == "invslot2")
                {
                    item.m_requiredGlobalKey = "defeated_bonemass";
                    second = item;
                }
                else if (item.m_buyKey == ThirdPurchaseKey) third = true;
            }
            if (second == null || third) return;
            // The game buys, charges, saves and hides it as it does the other two, and adds the row through its own key.
            __instance.m_items.Add(new Trader.TradeItem
            {
                m_price = 3000,
                m_requiredGlobalKey = "defeated_goblinking",
                m_levelUpEffect = second.m_levelUpEffect,
                m_buyPlayerEffects = second.m_buyPlayerEffects,
                m_icon = second.m_icon,
                m_name = "Roomier pockets",
                m_tooltip = "Permanently adds one inventory row (8 slots).",
                m_buyKey = ThirdPurchaseKey,
                m_incrementKey = "invrows",
                m_incrementAmount = 1
            });
        }
    }
}
