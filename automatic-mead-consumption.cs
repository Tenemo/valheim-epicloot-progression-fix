using System;
using System.Collections.Generic;
using System.Reflection;
using EpicLoot;
using EpicLoot.Magic.MagicItemEffects;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class AutomaticMeadPrediction
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(AutoMeads), nameof(AutoMeads.OnIncomingHit));
            yield return AccessTools.Method(typeof(AutoMeads).GetNestedType("Character_ApplyDamage_Patch", BindingFlags.NonPublic), "Prefix")
                ?? throw new InvalidOperationException("EpicLoot's automatic-mead tick prediction changed.");
        }
        // Replace the pre-hit estimate with the actual health check below.
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
    internal static class AutomaticMeadConsumption
    {
        private static void Prefix(Character __instance, out float __state)
        {
            __state = __instance == Player.m_localPlayer ? __instance.GetHealth() : 0f;
        }

        private static void Postfix(Character __instance, float __state)
        {
            if (!(__instance is Player player) || player != Player.m_localPlayer) return;
            float health = player.GetHealth();
            if (health <= 0f || health >= __state || player.IsDead()
                || !player.HasActiveMagicEffect(MagicEffectType.AutoMead)
                || !ModifyWithLowHealth.PlayerHasLowHealth(player)) return;

            var inventory = player.GetInventory();
            if (inventory == null) return;
            var statusEffects = player.GetSEMan();
            foreach (var item in inventory.GetAllItemsOfType(ItemDrop.ItemData.ItemType.Consumable))
            {
                if (!AutoMeads.HasHealthRegen(item)) continue;
                var effect = item.m_shared.m_consumeStatusEffect;
                if (statusEffects.HaveStatusEffect(effect.NameHash())
                    || statusEffects.HaveStatusEffectCategory(effect.m_category)) continue;
                if (player.ConsumeItem(inventory, item)) break;
            }
        }
    }
}
