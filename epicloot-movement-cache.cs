using System.Runtime.CompilerServices;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class EpicLootMovementCache
    {
        private sealed class Equipment { internal float RemovedPenalty; }
        private static ConditionalWeakTable<Player, Equipment> Players = new ConditionalWeakTable<Player, Equipment>();

        [HarmonyPrefix, HarmonyPatch(typeof(ModifyMovementSpeed), "GetModifyMovementSpeedAmount")]
        private static bool Movement(Player __0, ref float __result)
        {
            // Tooltip and deferred-hit hand overrides evaluate their own equipment, outside the live equipment cache.
            if (!__0 || HandOverride.Active != null) return true;
            __result = Players.GetValue(__0, ReadEquipment).RemovedPenalty + SpeedBonus(__0);
            return false;
        }

        // Movement speed and its Health Critical form, without Unhindered's removed penalty.
        internal static float SpeedBonus(Player player)
        {
            float bonus = player.GetTotalActiveMagicEffectValue(MagicEffectType.ModifyMovementSpeed, .01f);
            if (ModifyWithLowHealth.PlayerHasLowHealth(player))
                bonus += player.GetTotalActiveMagicEffectValue(ModifyWithLowHealth.EffectNameWithLowHealth(MagicEffectType.ModifyMovementSpeed), .01f);
            return bonus;
        }

        private static Equipment ReadEquipment(Player player)
        {
            var result = new Equipment();
            foreach (var item in player.GetMagicEquipment())
                if (item != null && item.HasMagicEffect(MagicEffectType.RemoveSpeedPenalty))
                    result.RemovedPenalty -= Mathf.Clamp(item.m_shared.m_movementModifier, float.MinValue, 0f);
            return result;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(EquipmentEffectCache), nameof(EquipmentEffectCache.Reset))]
        private static void Changed(Player player) { if (player) Players.Remove(player); }

        [HarmonyPostfix, HarmonyPatch(typeof(EquipmentEffectCache), nameof(EquipmentEffectCache.ResetAll))]
        private static void ChangedAll() => Players = new ConditionalWeakTable<Player, Equipment>();
    }
}
