using System.Runtime.CompilerServices;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using EpicLoot.src.Magic.MagicItemEffects.Helpers;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // EpicLoot scales only the first chain lightning strike by the rolled value. Each jump copies its settings from
    // the strike's hit settings, which EpicLoot never sets, so jumps deal the prefab's full damage whatever the roll.
    // The 80% chain chance and falloff EpicLoot sets are never used either: the game rolls every jump against the
    // prefab's start chance. Give the strike hit settings with the scaled damage, so every jump deals it, and roll each
    // jump against EpicLoot's chance, which falls by a fifth per jump.
    [HarmonyPatch]
    internal static class ChainLightningJumps
    {
        private static readonly AccessTools.FieldRef<Aoe, HitData> SettingsOf = AccessTools.FieldRefAccess<Aoe, HitData>("m_hitData");
        private static readonly AccessTools.FieldRef<Aoe, float> ChainChanceOf = AccessTools.FieldRefAccess<Aoe, float>("m_chainChance");
        private static readonly AccessTools.FieldRef<Aoe, int> StatusEffectOf = AccessTools.FieldRefAccess<Aoe, int>("m_statusEffectHash");
        // Every strike and jump of one chain shares its hit settings, which identify it as EpicLoot's.
        private static readonly ConditionalWeakTable<HitData, object> Chains = new ConditionalWeakTable<HitData, object>();
        private static readonly object Chain = new object();

        [HarmonyPatch(typeof(ChainLightning), "TriggerChainLightningEffect"), HarmonyPrefix]
        private static bool Strike(Character target, Player player)
        {
            var prefab = ZNetScene.instance.GetPrefab("ChainLightning");
            var strike = prefab == null ? null : Object.Instantiate(prefab, RangedHitConfirmation.Position(target), Quaternion.identity).GetComponent<Aoe>();
            if (strike == null) return false;
            // As EpicLoot does, so the strike and its jumps are bonus hits rather than weapon strikes.
            HitSource.MarkBonusSource(strike.gameObject);
            GameFields.AoeOwner(strike) = player;
            ChainChanceOf(strike) = 0.8f;
            strike.m_chainStartChanceFalloff = 0.8f;
            strike.m_damage.m_lightning *= player.GetTotalActiveMagicEffectValue(MagicEffectType.ChainLightning, 0.01f);
            // Jumps take everything else from these settings too, so keep the strike's own values. Jumps without
            // settings neither scaled with nor trained a skill, and still don't.
            var settings = new HitData
            {
                m_damage = strike.m_damage,
                m_blockable = strike.m_blockable,
                m_dodgeable = strike.m_dodgeable,
                m_pushForce = strike.m_attackForce,
                m_backstabBonus = strike.m_backstabBonus,
                m_statusEffectHash = StatusEffectOf(strike),
                m_toolTier = (short)strike.m_toolTier,
                m_skill = Skills.SkillType.All,
            };
            SettingsOf(strike) = settings;
            Chains.Add(settings, Chain);
            return false;
        }

        // The game passes each jump its parent's chance times the falloff, but rolls against the start chance. Only
        // strikes that can jump at all get the passed chance, so a prefab that never jumps still doesn't.
        [HarmonyPatch(typeof(Aoe), nameof(Aoe.CustomFixedUpdate)), HarmonyPrefix]
        private static void RollChainChance(Aoe __instance)
        {
            if (__instance.m_chainStartChance > 0f && __instance.m_chainObj != null && IsChain(__instance))
                __instance.m_chainStartChance = ChainChanceOf(__instance);
        }

        // Whether an area effect is an EpicLoot chain lightning strike or one of its jumps.
        internal static bool IsChain(Aoe aoe)
        {
            var settings = SettingsOf(aoe);
            return settings != null && Chains.TryGetValue(settings, out _);
        }
    }
}
