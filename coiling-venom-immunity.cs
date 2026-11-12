using EpicLoot;
using EpicLoot.src.Magic.MagicItemEffects.Helpers;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Coiling Venom converts a share of a weapon's physical damage into poison before it knows the target, so a
    // poison-immune creature took only what was left of the physical damage. Undoes the conversion on such a hit, before
    // EpicLoot tags it as venom. Poison the weapon deals itself stays.
    internal static class CoilingVenomImmunity
    {
        private const float Tolerance = .01f;

        internal static void Initialize(IntegrationPatches harmony) =>
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.Damage)),
                prefix: new HarmonyMethod(typeof(CoilingVenomImmunity), nameof(RestorePhysicalDamage)) { priority = Priority.First });

        private static void RestorePhysicalDamage(Character __instance, HitData hit)
        {
            var player = Player.m_localPlayer;
            if (hit == null || !player || HitSource.IsBonusHit || hit.GetAttacker() != player ||
                __instance.GetDamageModifiers().m_poison != HitData.DamageModifier.Immune) return;
            float share = Mathf.Clamp01(player.GetTotalActiveMagicEffectValue(MagicEffectType.CoilingVenom, 0.01f));
            float physical = hit.m_damage.m_blunt + hit.m_damage.m_slash + hit.m_damage.m_pierce;
            if (share <= 0f || share >= 1f || physical <= 0f) return;
            float converted = physical * share / (1f - share);
            if (hit.m_damage.m_poison < converted - Tolerance) return;
            float restored = 1f / (1f - share);
            hit.m_damage.m_blunt *= restored;
            hit.m_damage.m_slash *= restored;
            hit.m_damage.m_pierce *= restored;
            float own = hit.m_damage.m_poison - converted;
            hit.m_damage.m_poison = own > Tolerance ? own : 0f;
        }
    }
}
