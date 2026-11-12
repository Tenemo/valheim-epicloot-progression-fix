using System.Runtime.CompilerServices;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class QueuedBowCancellation
    {
        // A cancelled attack may receive a delayed animation callback even after it stops being current.
        private static readonly ConditionalWeakTable<Attack, object> Cancelled = new ConditionalWeakTable<Attack, object>();
        private static readonly object CancelledMarker = new object();
        private static Player queuedPlayer;
        private static Attack queuedAttack;

        internal static void Track(Player player, Attack attack)
        {
            queuedPlayer = player;
            queuedAttack = attack;
        }

        private static void ClearPending()
        {
            queuedPlayer = null;
            queuedAttack = null;
        }

        // An attack abandoned elsewhere, such as by raising the guard, never fires either.
        internal static void Abandon(Attack attack)
        {
            if (!Cancelled.TryGetValue(attack, out _)) Cancelled.Add(attack, CancelledMarker);
            if (attack == queuedAttack) ClearPending();
        }

        private static void CheckCancellation()
        {
            if (queuedAttack == null) return;
            if (queuedPlayer && queuedPlayer == Player.m_localPlayer && !queuedPlayer.IsDead() &&
                GameFields.CurrentAttack(queuedPlayer) == queuedAttack && queuedPlayer.GetCurrentWeapon() == queuedAttack.GetWeapon() &&
                !GameFields.Blocking(queuedPlayer)) return;
            CancelPending();
        }

        private static void CancelPending()
        {
            if (queuedAttack == null) return;
            Cancelled.Add(queuedAttack, CancelledMarker);
            queuedAttack.Abort();
            queuedAttack.Stop();
            // Keep the native recovery animation and its current attack reference, so old events cannot fire a new shot.
            ClearPending();
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPrefix]
        private static void Leaving() => CancelPending();

        [HarmonyPatch(typeof(Player), nameof(Player.SetControls)), HarmonyPostfix]
        private static void ControlsUpdated(Player __instance)
        {
            if (__instance == queuedPlayer || queuedPlayer != Player.m_localPlayer) CheckCancellation();
        }

        [HarmonyPatch(typeof(Attack), nameof(Attack.OnAttackTrigger)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool BeforeTrigger(Attack __instance)
        {
            CheckCancellation();
            if (Cancelled.TryGetValue(__instance, out _)) return false;
            // The firing callback is the commitment point; blocking after launch cannot recall its projectiles.
            if (__instance == queuedAttack) ClearPending();
            return true;
        }

        // Harmony still runs void prefixes without ref arguments when an earlier prefix skips the original.
        // Suppress Triple Shot's prefix itself, before it selects a volley or changes its global pending state.
        [HarmonyPatch(typeof(MultiShot), nameof(MultiShot.Attack_OnAttackTrigger_Prefix)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool BeforeMultiShot(Attack __0) => !Cancelled.TryGetValue(__0, out _);
    }
}
