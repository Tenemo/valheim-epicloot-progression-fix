using System.Collections.Generic;
using EpicLoot;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot ranks an enchant's rarity stars by its roll weight alone, which ignores what blocks it, what it needs first
    // and the unique legendaries that always carry it. These tiers come from each enchant's actual chance on a newly
    // enchanted item, averaged over Magic to Mythic and over the items it can roll on, unique legendaries and set pieces
    // included: 3 stars under 0.3%, 2 under 0.5% and 1 under 2%. Every star EpicLoot draws goes through GetTier.
    // Recalculate the list when effect weights, requirements or unique legendaries change.
    [HarmonyPatch(typeof(MagicEffectRarity), nameof(MagicEffectRarity.GetTier))]
    internal static class EnchantStarTiers
    {
        private static readonly Dictionary<string, int> Tiers = new Dictionary<string, int>
        {
            ["FreeBuild"] = 3,
            ["Apportation"] = 3,
            ["AddElementalResistancePercentage"] = 3,
            ["DoubleJump"] = 2,
            ["FeatherFall"] = 2,
            ["Duelist"] = 1,
            ["AddPhysicalResistancePercentage"] = 1,
            ["Warmth"] = 1,
            ["Waterproof"] = 1,
            ["Executioner"] = 1,
            ["Indestructible"] = 1,
            ["Comfortable"] = 1,
            ["DecreaseForsakenCooldown"] = 1,
            ["ModifyAttackHealthUse"] = 1,
            ["ModifyDodgeStaminaUse"] = 1,
            ["IncreaseHeatResistance"] = 1,
            ["SpellSword"] = 1,
            ["LifeAfterKill"] = 1,
            ["EitrWeave"] = 1,
            ["AddBluntResistancePercentage"] = 1,
            ["AddPiercingResistancePercentage"] = 1,
            ["AddSlashingResistancePercentage"] = 1,
            ["ModifyDamage"] = 1,
            ["ChainLightning"] = 1,
            ["LifeSteal"] = 1,
        };

        internal static int Get(string effectType) => effectType != null && Tiers.TryGetValue(effectType, out int tier) ? tier : 0;

        private static bool Prefix(MagicItemEffectDefinition effectDef, ref int __result)
        {
            __result = Get(effectDef?.Type);
            return false;
        }
    }
}
