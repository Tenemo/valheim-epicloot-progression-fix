using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Two faults kept dropped items from reaching the player.
    // Auto pickup collects the colliders on the item layer around the player into a fixed list of 100 and ignores the rest.
    // Many pickables share that layer: mushrooms, dandelions, thistle and the like. A garden inside EpicLoot's larger pickup
    // range filled the list, so dropped items there moved a few steps toward the player and then lay still, even beside them.
    // A pass that fills the list grows it for the next one, so a crowded spot misses items for one physics step at most.
    // The pull moves an item 15 m/s toward the chest but leaves its body's velocity alone, so gravity kept adding to it for as
    // long as the item flew. Pulled up a slope or after a running player, it fell at the pull's speed after about 1.5 s, hung
    // short of the player and dropped back down. Each step that moves an item now also stops its body.
    [HarmonyPatch(typeof(Player), "AutoPickup")]
    internal static class AutoPickupReach
    {
        private static void Postfix(ref Collider[] ___m_colliders)
        {
            if (___m_colliders.Length > 0 && ___m_colliders[___m_colliders.Length - 1] != null)
                ___m_colliders = new Collider[___m_colliders.Length * 4];
        }

        // Valheim 1.0.15 moves the item, and the terrain dummy of a floating one, with Transform.position's setter.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.PropertySetter(typeof(Transform), nameof(Transform.position))),
                AccessTools.Method(typeof(AutoPickupReach), nameof(Pull)), 2, "Valheim's auto pickup no longer moves items as expected.");

        private static void Pull(Transform transform, Vector3 position)
        {
            transform.position = position;
            var body = transform.GetComponent<Rigidbody>();
            if (body && !body.isKinematic) body.linearVelocity = Vector3.zero;
        }
    }
}
