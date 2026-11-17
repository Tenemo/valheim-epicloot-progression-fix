using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Staff of Fracturing's bolt and each of its 12 splinters carry the staff's whole push, and a splinter pushes the way it
    // flies. A creature caught in one burst was shoved up to 13 times in scattered directions, each hit restarting the game's
    // fixed-speed knockback slide, so it jittered about instead of being knocked back. Each creature is now pushed once per
    // cast: by the bolt when the bolt hits it, otherwise by the first splinter to reach it, away from where the bolt burst.
    // The cast's later hits on it keep their damage and push nothing. Projectiles hit on the caster's game, so each hit is
    // changed there before it is sent to the creature's owner.
    [HarmonyPatch]
    internal static class FracturingKnockback
    {
        private const string Bolt = "staff_clusterbombstaff_projectile";
        private const string Splinter = "staff_clusterbombstaff_splinter_projectile";

        // One cast: the creatures it has pushed.
        private sealed class Cast
        {
            internal readonly HashSet<Character> Pushed = new HashSet<Character>();
        }

        // A bolt, or a splinter with the point its bolt burst at.
        private sealed class Shot
        {
            internal readonly Cast Cast;
            internal readonly bool IsSplinter;
            internal readonly Vector3 Burst;
            internal Shot(Cast cast, bool isSplinter, Vector3 burst) { Cast = cast; IsSplinter = isSplinter; Burst = burst; }
        }

        private static readonly ConditionalWeakTable<Projectile, Shot> Shots = new ConditionalWeakTable<Projectile, Shot>();
        // The splinters a bursting bolt is setting up, and the projectile whose hit is being dealt.
        private static Shot bursting, hitting;

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup)), HarmonyPostfix]
        private static void Track(Projectile __instance)
        {
            string name = Utils.GetPrefabName(__instance.gameObject);
            Shot shot;
            if (name == Bolt) shot = new Shot(new Cast(), false, Vector3.zero);
            else if (name == Splinter && bursting != null) shot = bursting;
            else return;
            Shots.Remove(__instance);
            Shots.Add(__instance, shot);
        }

        // The bolt bursts where it hit something or where its flight timed out.
        [HarmonyPatch(typeof(Projectile), "SpawnOnHit"), HarmonyPrefix]
        private static void BurstStart(Projectile __instance, out Shot __state)
        {
            __state = bursting;
            if (Shots.TryGetValue(__instance, out var shot) && !shot.IsSplinter) bursting = new Shot(shot.Cast, true, __instance.transform.position);
        }

        [HarmonyPatch(typeof(Projectile), "SpawnOnHit"), HarmonyFinalizer]
        private static void BurstEnd(Shot __state) => bursting = __state;

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.OnHit)), HarmonyPrefix]
        private static void ImpactStart(Projectile __instance, out Shot __state)
        {
            __state = hitting;
            hitting = Shots.TryGetValue(__instance, out var shot) ? shot : null;
        }

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.OnHit)), HarmonyFinalizer]
        private static void ImpactEnd(Shot __state) => hitting = __state;

        [HarmonyPatch(typeof(Character), nameof(Character.Damage)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void PushOnce(Character __instance, HitData hit)
        {
            var shot = hitting;
            if (shot == null || hit.m_pushForce <= 0f) return;
            if (!shot.Cast.Pushed.Add(__instance))
            {
                hit.m_pushForce = 0f;
                return;
            }
            if (!shot.IsSplinter) return;
            var away = __instance.transform.position - shot.Burst;
            away.y = 0f;
            if (away.sqrMagnitude > 1e-4f) hit.m_dir = away.normalized;
        }

        // Both projectiles are found by their prefab's name, so a renamed one stops the game loading instead of leaving the
        // staff's knockback as it was.
        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void RequireProjectiles(ZNetScene __instance)
        {
            var bolt = __instance.GetPrefab(Bolt)?.GetComponent<Projectile>();
            if (!bolt || !bolt.m_spawnOnHit || Utils.GetPrefabName(bolt.m_spawnOnHit) != Splinter || !bolt.m_spawnOnHit.GetComponent<Projectile>())
                throw new InvalidOperationException("Staff of Fracturing's bolt no longer bursts into its splinters.");
        }
    }
}
