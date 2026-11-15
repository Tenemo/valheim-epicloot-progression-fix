using System;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The native store greys out what the carried coins can't pay for only when it fills its list. Claiming bounties, gambling or
    // picking up coins with the window open left those rows grey, which read as unbuyable until the window was reopened.
    [HarmonyPatch(typeof(StoreGui))]
    internal static class TraderCoinRefresh
    {
        private static readonly Func<StoreGui, int> Coins = AccessTools.MethodDelegate<Func<StoreGui, int>>(AccessTools.Method(typeof(StoreGui), "GetPlayerCoins"));
        private static readonly Action<StoreGui> FillList = AccessTools.MethodDelegate<Action<StoreGui>>(AccessTools.Method(typeof(StoreGui), "FillList"));
        private static int listedCoins = -1;

        [HarmonyPatch("FillList"), HarmonyPostfix]
        private static void Listed(StoreGui __instance) => listedCoins = Coins(__instance);

        [HarmonyPatch("Update"), HarmonyPostfix]
        private static void Update(StoreGui __instance, Trader ___m_trader)
        {
            if (!__instance.m_rootPanel.activeSelf || !___m_trader || !Player.m_localPlayer) return;
            if (Coins(__instance) != listedCoins) FillList(__instance);
        }
    }
}
