using System;
using System.Collections.Generic;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class LegacyStarterGear
    {
        private static readonly Dictionary<int, int> MeadowsPrefabs = new Dictionary<int, int>();
        // The same pairs as arrays: the prefab lookups run hundreds of times a frame, and a few comparisons beat a table.
        private static int[] legacy = Array.Empty<int>(), meadows = Array.Empty<int>();

        internal static void Initialize(IntegrationPatches harmony)
        {
            foreach (ItemRarity rarity in Enum.GetValues(typeof(ItemRarity)))
                MeadowsPrefabs.Add($"None_{rarity}_Unidentified".GetStableHashCode(),
                    $"Meadows_{rarity}_Unidentified".GetStableHashCode());

            legacy = new int[MeadowsPrefabs.Count]; meadows = new int[MeadowsPrefabs.Count];
            MeadowsPrefabs.Keys.CopyTo(legacy, 0); MeadowsPrefabs.Values.CopyTo(meadows, 0);

            // Resolve the saved identity before the game clones the prefab and restores item data.
            var resolve = new HarmonyMethod(typeof(LegacyStarterGear), nameof(ResolvePrefab));
            harmony.Patch(AccessTools.Method(typeof(ObjectDB), nameof(ObjectDB.TryGetItemPrefab),
                new[] { typeof(int), typeof(GameObject).MakeByRefType() }), prefix: resolve);
            harmony.Patch(AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.GetPrefab),
                new[] { typeof(int) }), prefix: resolve);

            // World loading precedes ownership and sector registration. Change only the loaded
            // prefab field; SetPrefab would notify networking about a not-yet-registered object.
            harmony.Patch(AccessTools.Method(typeof(ZDO), nameof(ZDO.Load)),
                postfix: new HarmonyMethod(typeof(LegacyStarterGear), nameof(LoadWorldItem)));
        }

        private static void ResolvePrefab(ref int __0)
        {
            for (int index = 0; index < legacy.Length; index++)
                if (__0 == legacy[index]) { __0 = meadows[index]; return; }
        }

        private static void LoadWorldItem(ref int ___m_prefab)
        {
            if (MeadowsPrefabs.TryGetValue(___m_prefab, out int meadows)) ___m_prefab = meadows;
        }
    }
}
