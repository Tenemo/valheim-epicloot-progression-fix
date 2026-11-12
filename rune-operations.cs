using System;
using EpicLoot;
using EpicLoot.CraftingV2;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(EnchantingUIController))]
    internal static class RuneOperations
    {
        private static readonly Action<string, Action> WithReason = AccessTools.MethodDelegate<Action<string, Action>>(
            AccessTools.Method(typeof(API), "WithChangeReason", new[] { typeof(string), typeof(Action) }));

        // Transfer the complete affix, applying only extraction power that it has not already received.
        [HarmonyPostfix, HarmonyPatch("BuildEnchantedRune")]
        private static void ExtractedRunePower(ItemDrop.ItemData selectedItem, int targetEnchant, float powerModifier,
            ItemDrop.ItemData __result)
        {
            if (__result == null) return;
            var effect = selectedItem.GetMagicItem().Effects[targetEnchant];
            var rune = __result.GetMagicItem();
            RuneExtractionPower.Extract(effect, rune.Effects[0], powerModifier);
            WithReason("Rune", () => __result.SaveMagicItem(rune));
            RuneStacks.Normalize(__result);
        }

        // EpicLoot keeps a socketed item magic after its last effect is extracted, but not a named or set item.
        [HarmonyPrefix, HarmonyPatch("ReduceItemAfterRuneExtract")]
        private static bool PreserveExtractedItem(ItemDrop.ItemData item, int targetEnchant, bool reduceRarity)
        {
            var magic = item.GetMagicItem();
            if (magic == null || targetEnchant != 0 || magic.Effects.Count != 1 || magic.SocketCount > 0 || magic.Sockets.Count > 0
                || (string.IsNullOrEmpty(magic.LegendaryID) && string.IsNullOrEmpty(magic.SetID))) return true;
            magic.RemoveEffectAt(0);
            if (reduceRarity && magic.Rarity > ItemRarity.Magic) magic.Rarity--;
            WithReason("Rune", () => item.SaveMagicItem(magic));
            return false;
        }
    }
}
