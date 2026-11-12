using System;
using System.Collections.Generic;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Trader), nameof(Trader.GetAvailableItems))]
    internal static class FeastSpiceAvailability
    {
        internal static readonly HashSet<string> Spices = new HashSet<string>(StringComparer.Ordinal)
        {
            "SpiceForests", "SpiceOceans", "SpiceMountains", "SpicePlains", "SpiceMistlands", "SpiceAshlands", "SpiceDeepNorth"
        };

        private static void Prefix(Trader __instance)
        {
            if (Utils.GetPrefabName(__instance.gameObject) != "BogWitch") return;
            foreach (var offer in __instance.m_items)
                if (offer.m_prefab && Spices.Contains(offer.m_prefab.name)) offer.m_requiredGlobalKey = "";
        }
    }
}
