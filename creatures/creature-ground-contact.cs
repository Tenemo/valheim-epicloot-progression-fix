using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Collision points are in world space, but the native height limits use the capsule's local radius and center.
    // A large tamed lox's narrowed capsule can then reject every floor contact and coast as if still airborne.
    [HarmonyPatch(typeof(Character), "OnCollisionStay")]
    internal static class CreatureGroundContact
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var radius = AccessTools.PropertyGetter(typeof(CapsuleCollider), nameof(CapsuleCollider.radius));
            var center = AccessTools.PropertyGetter(typeof(CapsuleCollider), nameof(CapsuleCollider.center));
            var code = Transpilers.CallInstead(instructions, instruction => instruction.Calls(radius),
                AccessTools.Method(typeof(CreatureGroundContact), nameof(ContactHeight)), 1, "The game's ground-contact height check changed.");
            return Transpilers.CallInstead(code, instruction => instruction.Calls(center),
                AccessTools.Method(typeof(CreatureGroundContact), nameof(CenterOffset)), 1, "The game's ground-contact center check changed.");
        }

        // The game asks both for every upward contact of one collision, and nothing in its loop moves or resizes the
        // capsule, so each is worked out once per collision.
        private static bool heightKnown, centerKnown;
        private static float height;
        private static Vector3 center;

        private static void Prefix() => heightKnown = centerKnown = false;

        private static float ContactHeight(CapsuleCollider collider)
        {
            if (heightKnown) return height;
            float scale = Mathf.Abs(collider.transform.lossyScale.y);
            float reach = collider.radius;
            // Size mods also shorten tame capsules without moving their centers. Their raised feet still count as
            // ground, through the lower cap; keep the original cutoff for unscaled characters.
            if (scale > 1f) reach = Mathf.Max(reach, collider.center.y - Mathf.Max(0f, collider.height * .5f - collider.radius));
            heightKnown = true;
            return height = reach * scale;
        }

        private static Vector3 CenterOffset(CapsuleCollider collider)
        {
            if (centerKnown) return center;
            centerKnown = true;
            return center = collider.transform.TransformVector(collider.center);
        }
    }
}
