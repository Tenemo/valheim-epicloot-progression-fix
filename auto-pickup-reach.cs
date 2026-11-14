using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Three faults kept dropped items from reaching the player.
    // Auto pickup collects the colliders on the item layer around the player into a fixed list of 100 and ignores the rest.
    // Many pickables share that layer: mushrooms, dandelions, thistle and the like. A garden inside EpicLoot's larger pickup
    // range filled the list, so dropped items there moved a few steps toward the player and then lay still, even beside them.
    // A pass that fills the list grows it for the next one, so a crowded spot misses items for one physics step at most.
    // The pull moves an item 15 m/s toward the chest but leaves its body's velocity alone, so gravity kept adding to it for as
    // long as the item flew. Pulled up a slope or after a running player, it fell at the pull's speed after about 1.5 s, hung
    // short of the player and dropped back down. Each step that moves an item now also stops its body.
    // The pull's 15 m/s is fixed, so it barely gained on a player running near that speed and never caught one running faster.
    // An item now also moves as fast as the player moved away from it in the last physics step, so it closes in at 15 m/s
    // whatever the player's speed, and as before when the player stands or comes toward it.
    [HarmonyPatch(typeof(Player), "AutoPickup")]
    internal static class AutoPickupReach
    {
        // A position change faster than this in one step is a jump the teleport check missed, not movement, so it adds nothing.
        private const float MaximumFollowSpeed = 50f;
        private static Player sampled;
        private static Vector3 sampledPosition, playerVelocity;
        private static float sampledTime;

        // The player's velocity over the last physics step, from its position, so whatever moved the player counts.
        private static void Prefix(Player __instance)
        {
            var position = __instance.transform.position;
            float now = Time.fixedTime, elapsed = now - sampledTime;
            playerVelocity = Vector3.zero;
            if (__instance.IsTeleporting()) { sampled = null; return; }
            if (__instance == sampled && elapsed > 0f && elapsed < Time.fixedDeltaTime * 1.5f)
            {
                playerVelocity = (position - sampledPosition) / elapsed;
                if (playerVelocity.sqrMagnitude > MaximumFollowSpeed * MaximumFollowSpeed) playerVelocity = Vector3.zero;
            }
            sampled = __instance; sampledPosition = position; sampledTime = now;
        }

        private static void Postfix(ref Collider[] ___m_colliders)
        {
            if (___m_colliders.Length > 0 && ___m_colliders[___m_colliders.Length - 1] != null)
                ___m_colliders = new Collider[___m_colliders.Length * 4];
        }

        // Valheim 1.0.15 moves the item, and the terrain dummy of a floating one, with Transform.position's setter.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.PropertySetter(typeof(Transform), nameof(Transform.position))),
                AccessTools.Method(typeof(AutoPickupReach), nameof(Pull)), 2, "Valheim's auto pickup no longer moves items as expected.");

        // The item and its terrain dummy get the same step, so they take the same follow distance.
        private static void Pull(Transform transform, Vector3 position)
        {
            var step = position - transform.position;
            float length = step.magnitude;
            if (length > 0f)
            {
                float away = Vector3.Dot(playerVelocity, step) / length;
                if (away > 0f) position += step * (away * Time.fixedDeltaTime / length);
            }
            transform.position = position;
            var body = transform.GetComponent<Rigidbody>();
            if (body && !body.isKinematic) body.linearVelocity = Vector3.zero;
        }
    }
}
