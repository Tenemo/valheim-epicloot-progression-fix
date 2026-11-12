using System;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using EpicLoot.MagicItemEffects.Shards;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Fixes for EpicLoot effects on hits the player blocks.
    [HarmonyPatch]
    internal static class BlockedHits
    {
        internal static readonly Func<Humanoid, ItemDrop.ItemData> CurrentBlocker =
            AccessTools.MethodDelegate<Func<Humanoid, ItemDrop.ItemData>>(AccessTools.Method(typeof(Humanoid), "GetCurrentBlocker"));

        // Whether the game's block step will handle this hit: a blockable hit from the front while blocking with an item.
        internal static bool WillBlock(Character character, HitData hit) =>
            character is Humanoid humanoid && hit.m_blockable && humanoid.IsBlocking()
            && Vector3.Dot(hit.m_dir, humanoid.transform.forward) <= 0f && CurrentBlocker(humanoid) != null;

        // EpicLoot converts a blocked hit's on-block physical-to-elemental share after the block, and since blocks now
        // come after resistances, converted damage would skip the player's elemental resistances. Block conversion
        // runs before them instead, from BlockAfterArmor.
        [HarmonyPatch(typeof(IncomingPhysicalConversion), nameof(IncomingPhysicalConversion.ModifyIncoming)), HarmonyPrefix]
        private static bool GeneralConversionOnly(bool IsBlocked) => !IsBlocked;

        // EpicLoot's resistance enchantments, as it applies them when the hit arrives: what is left of a damage type.
        private static readonly Func<Player, string, float, float> Unresisted = AccessTools.MethodDelegate<Func<Player, string, float, float>>(
            AccessTools.Method(typeof(ModifyResistance), "GetCappedResistanceValue"));

        // The general share has already been converted when the hit arrived, so convert the on-block share of the
        // original physical damage, as EpicLoot does after the block. The resistance enchantments have also already
        // reduced the hit, so the converted part is taken from the physical damage before the physical ones and meets the
        // fire, frost, poison or lightning ones instead, like the general share.
        internal static void ConvertBeforeBlock(Player player, HitData hit)
        {
            if (player != Player.m_localPlayer || !WillBlock(player, hit)) return;
            float fire = player.GetTotalActiveMagicEffectValue(MagicEffectType.PhysToFireOnBlock, 0.01f);
            float frost = player.GetTotalActiveMagicEffectValue(MagicEffectType.PhysToFrostOnBlock, 0.01f);
            float poison = player.GetTotalActiveMagicEffectValue(MagicEffectType.PhysToPoisonOnBlock, 0.01f);
            float lightning = player.GetTotalActiveMagicEffectValue(MagicEffectType.PhysToLightningOnBlock, 0.01f);
            float onBlock = fire + frost + poison + lightning;
            float physical = hit.m_damage.m_blunt + hit.m_damage.m_slash + hit.m_damage.m_pierce;
            if (onBlock <= 0f || physical <= 0f) return;
            // The physical damage left is the part of the original that the general share did not convert.
            float left = 1f - player.GetTotalActiveMagicEffectValue(MagicEffectType.PhysToFire, 0.01f)
                - player.GetTotalActiveMagicEffectValue(MagicEffectType.PhysToFrost, 0.01f)
                - player.GetTotalActiveMagicEffectValue(MagicEffectType.PhysToPoison, 0.01f)
                - player.GetTotalActiveMagicEffectValue(MagicEffectType.PhysToLightning, 0.01f);
            if (left <= 0f) return;
            float share = Mathf.Min(1f, onBlock / left);
            float physicalBonus = player.GetTotalActiveMagicEffectValue(MagicEffectType.AddPhysicalResistancePercentage, 0.01f);
            float Unreduced(float damage, string effect)
            {
                float rest = Unresisted(player, effect, physicalBonus);
                return rest > 0f ? damage / rest : 0f;
            }
            float converted = (Unreduced(hit.m_damage.m_blunt, MagicEffectType.AddBluntResistancePercentage)
                + Unreduced(hit.m_damage.m_slash, MagicEffectType.AddSlashingResistancePercentage)
                + Unreduced(hit.m_damage.m_pierce, MagicEffectType.AddPiercingResistancePercentage)) * share / onBlock;
            float elemental = player.GetTotalActiveMagicEffectValue(MagicEffectType.AddElementalResistancePercentage, 0.01f);
            hit.m_damage.m_fire += converted * fire * Unresisted(player, MagicEffectType.AddFireResistancePercentage, elemental);
            hit.m_damage.m_frost += converted * frost * Unresisted(player, MagicEffectType.AddFrostResistancePercentage, elemental);
            hit.m_damage.m_poison += converted * poison * Unresisted(player, MagicEffectType.AddPoisonResistancePercentage, elemental);
            hit.m_damage.m_lightning += converted * lightning * Unresisted(player, MagicEffectType.AddLightningResistancePercentage, elemental);
            hit.m_damage.m_blunt *= 1f - share;
            hit.m_damage.m_slash *= 1f - share;
            hit.m_damage.m_pierce *= 1f - share;
        }

        // Feint skips the whole hit, block included, so a block or parry it lands on loses its stagger, experience and
        // adrenaline. Don't roll it while the player is blocking the hit with stamina left; without stamina the guard
        // breaks anyway, so Feint still rolls.
        [HarmonyPatch(typeof(AvoidDamageTaken_Character_RPC_Damage_Patch), nameof(AvoidDamageTaken_Character_RPC_Damage_Patch.ShouldTakeDamage))]
        private static class NoFeintWhileBlocking
        {
            private static bool Prefix(Character __0, HitData hit, ref bool __result)
            {
                if (hit == null || !WillBlock(__0, hit) || !__0.HaveStamina()) return true;
                __result = true;
                return false;
            }
        }
    }
}
