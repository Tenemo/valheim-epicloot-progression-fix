using System;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot raised each worn piece's armor by that piece's own Armor rolls, so +9% trousers and a +12% chest raised only
    // themselves. Every worn piece now takes the wearer's whole Armor total, EpicLoot's sum of the worn rolls, runes and set
    // bonuses, plus the critical-health Armor total while health is critical: 100 armor with +9% and +12% becomes 121. An
    // unworn piece still previews its own rolls, and the daylight, heavy-gear and dark-red shard multipliers keep their own
    // patches.
    internal static class TotalArmor
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            var perPiece = AccessTools.Method(typeof(ModifyArmor_ItemData_GetArmor_Patch), "Postfix");
            if (perPiece == null) throw new InvalidOperationException("EpicLoot's Armor calculation moved.");
            harmony.Patch(perPiece, prefix: new HarmonyMethod(typeof(TotalArmor), nameof(WornTotal)));
        }

        private static bool WornTotal(ItemDrop.ItemData __0, ref float __1)
        {
            var player = EpicLoot.PlayerExtensions.GetPlayerWithEquippedItem(__0);
            if (player == null) return true;
            float bonus = player.GetTotalActiveMagicEffectValue(MagicEffectType.ModifyArmor, 0.01f);
            if (ModifyWithLowHealth.PlayerHasLowHealth(player))
                bonus += player.GetTotalActiveMagicEffectValue(ModifyWithLowHealth.EffectNameWithLowHealth(MagicEffectType.ModifyArmor), 0.01f);
            __1 *= 1f + bonus;
            return false;
        }
    }
}
