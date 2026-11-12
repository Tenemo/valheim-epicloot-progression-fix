using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot calls this while calculating base block power. Its three flat stone bonuses are already included;
    // Duelist joins them before percentage block bonuses, then the game applies Blocking skill and native parry once.
    [HarmonyPatch(typeof(ModifyBlock), nameof(ModifyBlock.GetBlockPowerValue))]
    internal static class BlockAdditions
    {
        private static bool Prefix(Player player, ItemDrop.ItemData item, float originalValue, bool didParry, ref float __result)
        {
            float bonus = ModifyBlock.GetMultiplier(player, MagicEffectType.ModifyBlockPower, item);
            if (didParry) bonus += ModifyBlock.GetMultiplier(player, MagicEffectType.ModifyParry, item);
            __result = (originalValue + ModifyBlock.GetDuelistBlockBonus(player, item)) * (1f + bonus);
            return false;
        }
    }
}
