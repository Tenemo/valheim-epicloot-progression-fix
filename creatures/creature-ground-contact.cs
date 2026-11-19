using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Collision points are in world space, but the native height limits use the capsule's local radius and center.
    // A large tamed lox's narrowed capsule can then reject every floor contact and coast as if still airborne.
    [HarmonyPatch(typeof(Character), "OnCollisionStay")]
    internal static class CreatureGroundContact
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var radius = AccessTools.PropertyGetter(typeof(CapsuleCollider), nameof(CapsuleCollider.radius));
            var center = AccessTools.PropertyGetter(typeof(CapsuleCollider), nameof(CapsuleCollider.center));
            var code = Transpilers.CallInstead(instructions, instruction => instruction.Calls(radius),
                AccessTools.Method(typeof(CreatureGroundContact), nameof(ContactHeight)), 1, "The game's ground-contact height check changed.");
            code = Transpilers.CallInstead(code, instruction => instruction.Calls(center),
                AccessTools.Method(typeof(CreatureGroundContact), nameof(CenterOffset)), 1, "The game's ground-contact center check changed.");
            ReadContactsInPlace(code, generator);
            ReuseHitCollider(code, generator);
            return code;
        }

        // The game's foreach reads contacts in order. Read each one directly instead of asking
        // Unity to construct an array for this callback. GetContact also respects an existing contacts array.
        private static void ReadContactsInPlace(List<CodeInstruction> code, ILGenerator generator)
        {
            int get = code.FindIndex(i => i.Calls(AccessTools.PropertyGetter(typeof(Collision), nameof(Collision.contacts))));
            int read = code.FindIndex(i => i.opcode == OpCodes.Ldelem && Equals(i.operand, typeof(ContactPoint)));
            int length = code.FindIndex(i => i.opcode == OpCodes.Ldlen);
            if (get < 1 || !code[get - 1].IsLdarg(1) || get + 1 >= code.Count || code[get + 1].opcode != OpCodes.Stloc_0 ||
                read < get + 4 || code[read - 2].opcode != OpCodes.Ldloc_0 || !code[read - 1].IsLdloc() ||
                length <= read || code[length - 1].opcode != OpCodes.Ldloc_0 || code.Count(i => i.opcode == OpCodes.Ldloc_0) != 2)
                throw new InvalidOperationException("The character ground-contact loop changed.");

            var count = generator.DeclareLocal(typeof(int));
            code[get].operand = AccessTools.PropertyGetter(typeof(Collision), nameof(Collision.contactCount));
            code[get + 1].opcode = OpCodes.Stloc;
            code[get + 1].operand = count;
            code[read - 2].opcode = OpCodes.Ldarg_1;
            code[read].opcode = OpCodes.Callvirt;
            code[read].operand = AccessTools.Method(typeof(Collision), nameof(Collision.GetContact));
            code[length - 1].opcode = OpCodes.Ldloc;
            code[length - 1].operand = count;
            code[length].opcode = OpCodes.Nop;
        }

        // Checking the layer and recording support resolve the same collider that the null check just read.
        private static void ReuseHitCollider(List<CodeInstruction> code, ILGenerator generator)
        {
            var getter = AccessTools.PropertyGetter(typeof(Collision), nameof(Collision.collider));
            var calls = code.Where(i => i.Calls(getter)).ToList();
            if (calls.Count != 3 || calls.Any(call => code.IndexOf(call) < 1 || !code[code.IndexOf(call) - 1].IsLdarg(1)))
                throw new InvalidOperationException("The character ground-contact collider reads changed.");

            var hit = generator.DeclareLocal(typeof(Collider));
            foreach (var call in calls.Skip(1))
            {
                var argument = code[code.IndexOf(call) - 1];
                argument.opcode = OpCodes.Ldloc;
                argument.operand = hit;
                call.opcode = OpCodes.Nop;
                call.operand = null;
            }
            code.InsertRange(code.IndexOf(calls[0]) + 1, new[] { new CodeInstruction(OpCodes.Dup), new CodeInstruction(OpCodes.Stloc, hit) });
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
