using System;
using System.Runtime.CompilerServices;
using EpicLoot.MagicItemEffects.Shards;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Wager refunds its stake when the hit kills. When another game controls the target, the hit has not landed yet,
    // and EpicLoot estimates its damage without the sneak-attack bonus, weak spots or a player's body armor. Judge the
    // kill from the target's health after the hit when your game controls it, and otherwise from the health before it
    // and the damage the game will deal, with those as well as resistances, armor and scaling.
    [HarmonyPatch]
    internal static class WagerRefund
    {
        private static readonly AccessTools.FieldRef<HitData> StakedHit =
            AccessTools.StaticFieldRefAccess<HitData>(AccessTools.Field(typeof(Wager), "stakedHit"));
        private static readonly AccessTools.FieldRef<int> StakedCoins =
            AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(Wager), "stakedCoins"));
        private static readonly Action<Player, int> Refund = AccessTools.MethodDelegate<Action<Player, int>>(
            AccessTools.Method(typeof(Wager).Assembly.GetType("EpicLoot.General.CoinPurse"), "Refund"));
        private static readonly Func<Character, short, WeakSpot> WeakSpotOf =
            AccessTools.MethodDelegate<Func<Character, short, WeakSpot>>(AccessTools.Method(typeof(Character), "GetWeakSpot"));
        // The game gives a creature the sneak-attack bonus at most once every 300 seconds, apart from the rest of a
        // triple shot's volley, timed on the game that controls it. Keep the same timer here for this game's hits on
        // creatures other games control.
        private static readonly ConditionalWeakTable<Character, StrongBox<float>> SneakAttackTimes = new ConditionalWeakTable<Character, StrongBox<float>>();
        private static HitData measuredHit;
        private static float healthBeforeHit;
        private static bool sneakAttack;

        [HarmonyPatch(typeof(Wager), nameof(Wager.ModifyOutgoingHit)), HarmonyPostfix]
        private static void RecordHealth(Character __0, HitData hit, Character attacker)
        {
            if (hit == null || __0 == null) return;
            bool sneak = !__0.IsOwner() && TakesSneakAttackBonus(__0, hit, attacker);
            if (hit != StakedHit()) return;
            measuredHit = hit;
            healthBeforeHit = __0.GetHealth();
            sneakAttack = sneak;
        }

        // Follows Character.RPC_Damage with the add-on's volley rule, and starts the timer when the bonus applies.
        private static bool TakesSneakAttackBonus(Character target, HitData hit, Character attacker)
        {
            var ai = target.GetBaseAI();
            bool volley = VolleySneakAttack.Continues(target, hit);
            if (ai == null || (ai.IsAlerted() && !volley) || !(hit.m_backstabBonus > 1f)) return false;
            if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.PassiveMobs) && attacker != null && ai.CanSeeTarget(attacker)) return false;
            var time = SneakAttackTimes.GetValue(target, _ => new StrongBox<float>(float.NegativeInfinity));
            if (!volley && Time.time - time.Value <= 300f) return false;
            time.Value = Time.time;
            VolleySneakAttack.Record(target, hit);
            return true;
        }

        [HarmonyPatch(typeof(Wager), nameof(Wager.OnDamageDealt)), HarmonyPrefix]
        private static bool RefundKill(Character __0, HitData hit)
        {
            var stakedHit = StakedHit();
            int stakedCoins = StakedCoins();
            bool measured = measuredHit != null && measuredHit == hit;
            StakedHit() = null;
            StakedCoins() = 0;
            measuredHit = null;
            if (stakedCoins > 0 && stakedHit != null && stakedHit == hit && measured && __0 != null && Kills(__0, hit))
                Refund(Player.m_localPlayer, stakedCoins);
            return false;
        }

        private static bool Kills(Character target, HitData hit)
        {
            if (!(healthBeforeHit > 0f)) return false;
            // When this game controls the target, the hit has already landed.
            if (target.IsOwner()) return target.GetHealth() <= 0f;
            return healthBeforeHit - LandingDamage(target, hit) <= 0f;
        }

        // The damage the hit will deal on the game that controls the target, following Character.RPC_Damage and
        // ApplyDamage. The target's blocks and status effects, and the share StarLevelSystem's LifeLink passes on, are
        // not known here and are left out.
        private static float LandingDamage(Character target, HitData hit)
        {
            var landing = hit.Clone();
            if (sneakAttack) landing.ApplyModifier(landing.m_backstabBonus);
            if (!target.IsPlayer() && target.IsStaggering()) landing.ApplyModifier(2f);
            landing.ApplyResistance(target.GetDamageModifiers(WeakSpotOf(target, landing.m_weakSpot)), out _);
            if (target.IsPlayer()) landing.ApplyArmor(target.GetBodyArmor());
            else if (Game.m_worldLevel > 0) landing.ApplyArmor(Game.m_worldLevel * Game.instance.m_worldLevelEnemyBaseAC);
            // Poison, fire and spirit hurt over time instead of with the hit.
            landing.m_damage.m_poison = 0f;
            landing.m_damage.m_fire = 0f;
            landing.m_damage.m_spirit = 0f;
            if (!target.IsPlayer())
            {
                // Includes StarLevelSystem's group scaling, which replaces this difficulty scale.
                landing.ApplyModifier(Game.instance.GetDifficultyDamageScaleEnemy(target.transform.position));
                landing.ApplyModifier(Game.m_playerDamageRate);
            }
            float damage = landing.GetTotalDamage();
            return damage > 0.1f ? damage : 0f;
        }
    }
}
