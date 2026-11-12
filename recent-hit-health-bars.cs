using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class RecentHitHealthBars
    {
        private static readonly Dictionary<Character, float> Expiry = new Dictionary<Character, float>();

        // Character.Damage is the outgoing contact, before the remote confirmation path replays EpicLoot effects.
        // Each impact starts its own target's deadline here, never when a delayed reward or damage tick arrives.
        internal static void Record(Character target) => Expiry[target] = Time.time + 30f;

        internal static bool Revealed(Character target)
        {
            if (Expiry.Count == 0 || !target || !Expiry.TryGetValue(target, out float until)) return false;
            var player = Player.m_localPlayer;
            if (Time.time >= until || target.IsDead() || !player || player.IsDead())
            {
                Expiry.Remove(target);
                return false;
            }
            return !target.m_hideHud && !SummonedBats.IsBat(target) && !FreshCreatureSpawns.Waiting(target) &&
                SummonedBats.WithinSimulation(player, target);
        }

        [HarmonyPatch(typeof(EnemyHud), "TestShow")]
        private static class Eligibility
        {
            private static void Postfix(Character c, ref bool __result)
            {
                if (SummonedBats.IsBat(c)) __result = false;
                else if (Revealed(c)) __result = true;
            }
        }

        [HarmonyPatch(typeof(Character), "OnDestroy")]
        private static class Destroyed
        {
            private static void Prefix(Character __instance) => Expiry.Remove(__instance);
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        private static class Leaving
        {
            private static void Prefix() => Expiry.Clear();
        }

        [HarmonyPatch(typeof(Player), "OnDeath")]
        private static class Died
        {
            private static void Prefix(Player __instance)
            {
                if (__instance == Player.m_localPlayer) Expiry.Clear();
            }
        }
    }
}
