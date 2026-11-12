using System;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Spellblade charged its eitr on any sword but paid its damage bonus only on magic ones, so a plain sword paid for nothing.
    internal static class SpellbladeMagicSwords
    {
        private static readonly Func<ItemDrop.ItemData, bool> IsSpellswordWeapon = AccessTools.MethodDelegate<Func<ItemDrop.ItemData, bool>>(
            AccessTools.Method(typeof(Spellsword), "IsSpellswordWeapon"));
        private static bool askingForEnchantment;

        internal static void Initialize(IntegrationPatches harmony) =>
            harmony.Patch(AccessTools.Method(typeof(Spellsword), "IsSpellswordWeapon"),
                postfix: new HarmonyMethod(typeof(SpellbladeMagicSwords), nameof(RequireMagic)));

        // Enchanting asks about a sword before it is magic; the effect it rolls makes it magic.
        internal static bool IsSpellswordOnceMagic(ItemDrop.ItemData item)
        {
            askingForEnchantment = true;
            try { return IsSpellswordWeapon(item); }
            finally { askingForEnchantment = false; }
        }

        private static void RequireMagic(ItemDrop.ItemData weapon, ref bool __result) => __result &= askingForEnchantment || weapon != null && weapon.IsMagic();
    }
}
