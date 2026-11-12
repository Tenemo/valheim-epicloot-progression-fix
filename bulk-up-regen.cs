using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Bulk contributes its full signed regeneration penalty and uncapped health gain. The regeneration sum is floored
    // only after every source has contributed, so positive bonuses can offset even a penalty greater than 100 percent.
    [HarmonyPatch(typeof(BulkupEffect), "GetHealthRegenWithBulkUp")]
    internal static class BulkUpRegen
    {
        private static bool Prefix(float regen, ref float __result)
        {
            __result = Player.m_localPlayer.HasActiveMagicEffect(MagicEffectType.BulkUp, out float bulk, 0.01f)
                ? regen - bulk : regen;
            return false;
        }

        [HarmonyPatch(typeof(BulkupEffect), "GetAdditionalHealthWithBulkUp")]
        private static class AdditionalHealth
        {
            private static bool Prefix(float health, ref float __result)
            {
                __result = Player.m_localPlayer.HasActiveMagicEffect(MagicEffectType.BulkUp, out float bulk, .01f) ? health * bulk : 0f;
                return false;
            }
        }
    }
}
