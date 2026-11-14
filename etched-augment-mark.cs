using EpicLoot;
using EpicLoot.CraftingV2;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot remembers augmented and tempered effects by their slot. Etching a rune into a slot cleared only its tempered
    // mark, so the etched effect showed the old augment's triangle and could be augmented again without the surcharge a
    // fresh slot pays. The etched slot now loses its augmented mark too.
    [HarmonyPatch(typeof(EnchantingUIController), "SetEtchedEffect")]
    internal static class EtchedAugmentMark
    {
        private static void Postfix(MagicItem __0, int __1)
        {
            __0.AugmentedEffectIndices.RemoveAll(index => index == __1);
            if (__0.AugmentedEffectIndex == __1) __0.AugmentedEffectIndex = -1;
        }
    }
}
