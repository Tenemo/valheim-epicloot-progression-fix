using System;
using EpicLoot.MagicItemEffects.Shards;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The Yagluth meteor is a native projectile, which ends at the first thing it touches. It now ignores everything in
    // flight, water and shield domes included, and explodes at the target's centre as it was on the triggering hit. Its blast
    // spares what players built and the plants they planted that are still growing; world pieces, wild and grown plants,
    // trees, rocks and creatures take it as before. Yagluth's own meteors never carry the tag.
    internal sealed class MeteorFlight : MonoBehaviour
    {
        private static Vector3? called;
        private static bool blasting;
        private Vector3 destination, direction;
        private bool arrived;

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(MeteorFlight);
            void Patch(Type type, string method, string prefix = null, string postfix = null, string finalizer = null, int priority = Priority.Normal)
            {
                var original = AccessTools.Method(type, method) ?? throw new InvalidOperationException("Meteor flight hook moved: " + type.Name + "." + method);
                harmony.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(self, prefix) { priority = priority },
                    postfix: postfix == null ? null : new HarmonyMethod(self, postfix),
                    finalizer: finalizer == null ? null : new HarmonyMethod(self, finalizer));
            }
            Patch(typeof(MeteorSummoner), "SummonMeteor", prefix: nameof(Call), finalizer: nameof(EndCall));
            Patch(typeof(Projectile), nameof(Projectile.Setup), postfix: nameof(Launch));
            // Last, so the other mods' per-hit prefixes run as they do for every refused contact and their finalizers stay paired.
            Patch(typeof(Projectile), nameof(Projectile.OnHit), prefix: nameof(HitAllowed), priority: Priority.Last);
            Patch(typeof(ShieldGenerator), nameof(ShieldGenerator.CheckProjectile), prefix: nameof(ShieldApplies));
            // Damage runs on the caster's game before it sends the hit, so a skipped one shows no numbers and stirs no ward.
            Patch(typeof(Projectile), "DoAOE", prefix: nameof(BlastStarted), finalizer: nameof(BlastEnded));
            Patch(typeof(WearNTear), nameof(WearNTear.Damage), prefix: nameof(Struck));
            Patch(typeof(Destructible), nameof(Destructible.Damage), prefix: nameof(Struck));
        }

        private static void BlastStarted(Projectile __instance) => blasting = __instance.TryGetComponent<MeteorFlight>(out _);
        private static Exception BlastEnded(Exception __exception)
        {
            blasting = false;
            return __exception;
        }

        // A growing plant's grown form is a new object without a builder, so it counts as wild.
        private static bool Struck(Component __instance) => !blasting ||
            !(__instance.TryGetComponent<Piece>(out var piece) && piece.IsPlacedByPlayer() || __instance.TryGetComponent<Plant>(out _));

        // A confirmed bow or crossbow hit calls this after its target may have moved or gone, so ask for the spot it was hit at.
        private static void Call(Character target) => called = RangedHitConfirmation.Center(target);
        private static void EndCall() => called = null;

        private static void Launch(Projectile __instance, Vector3 velocity)
        {
            if (!called.HasValue) return;
            var flight = __instance.gameObject.AddComponent<MeteorFlight>();
            flight.destination = called.Value;
            flight.direction = velocity.normalized;
            called = null;
        }

        private static bool HitAllowed(Projectile __instance) => !__instance.TryGetComponent<MeteorFlight>(out var flight) || flight.arrived;

        // A dome would delete the meteor after its refused hit, and a finished one would go off a second time while it lingers.
        private static bool ShieldApplies(Projectile projectile) => !projectile.TryGetComponent<MeteorFlight>(out _);

        private void FixedUpdate()
        {
            if (arrived || Vector3.Dot(destination - transform.position, direction) > 0f) return;
            arrived = true;
            GetComponent<Projectile>().OnHit(null, destination, false, Vector3.up);
        }
    }
}
