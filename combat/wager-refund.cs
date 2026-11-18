using System;
using EpicLoot.MagicItemEffects.Shards;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Only the game that applied the hit knows whether it killed, after blocks, shields and shared damage.
    [HarmonyPatch]
    internal static class WagerRefund
    {
        private static readonly AccessTools.FieldRef<HitData> StakedHit =
            AccessTools.StaticFieldRefAccess<HitData>(AccessTools.Field(typeof(Wager), "stakedHit"));
        private static readonly AccessTools.FieldRef<int> StakedCoins =
            AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(Wager), "stakedCoins"));
        private static readonly Action<Player, int> Refund = AccessTools.MethodDelegate<Action<Player, int>>(
            AccessTools.Method(typeof(Wager).Assembly.GetType("EpicLoot.General.CoinPurse"), "Refund"));
        private static HitData measuredHit;
        private static float healthBeforeHit;

        [HarmonyPatch(typeof(Wager), nameof(Wager.ModifyOutgoingHit)), HarmonyPostfix]
        private static void RecordHealth(Character __0, HitData hit, Character attacker)
        {
            if (hit == null || __0 == null) return;
            if (hit != StakedHit()) return;
            measuredHit = hit;
            healthBeforeHit = __0.GetHealth();
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
            if (stakedCoins <= 0 || stakedHit != hit || !measured || !__0) return false;
            if (!__0.IsOwner()) RangedHitConfirmation.ConfirmStake(__0, hit, stakedCoins);
            else if (healthBeforeHit > 0f && __0.GetHealth() <= 0f) Refund(Player.m_localPlayer, stakedCoins);
            return false;
        }
    }
}
