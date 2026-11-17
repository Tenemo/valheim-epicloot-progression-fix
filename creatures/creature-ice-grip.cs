using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class CreatureIceGrip
    {
        private static readonly AccessTools.FieldRef<Character, bool> IceShoes = AccessTools.FieldRefAccess<Character, bool>("m_iceShoes");
        private static readonly AccessTools.FieldRef<Character, bool> Slipping = AccessTools.FieldRefAccess<Character, bool>("m_slipping");
        private static readonly AccessTools.FieldRef<Character, Vector3> GroundNormal = AccessTools.FieldRefAccess<Character, Vector3>("m_lastGroundNormal");
        // cos 30 degrees: flat ice keeps the skating model for riders.
        private const float SteepGround = .866f;
        private static IEnumerable<MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(Character), "UpdateWalking"), AccessTools.Method(typeof(Character), "ApplySlippery")
        };

        internal static bool HasIceGrip(Character character)
        {
            if (IceShoes(character)) return true;
            if (!character.IsOnGround()) return false;
            if (SummonOwnership.IsFollower(character)) return true;
            // Skating gives a ridden mount no push up a shore ice edge, so it could only turn back.
            if (character.HaveRider() && GroundNormal(character).y < SteepGround) return true;
            return !character.IsPlayer() && character.IsOwner() && !character.HaveRider() && !character.IsFlying() && !character.IsSwimming() &&
                character.GetBaseAI() && (character.m_speed > 0f || character.m_runSpeed > 0f);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            var shoes = AccessTools.Field(typeof(Character), "m_iceShoes");
            var reads = code.Where(instruction => instruction.LoadsField(shoes)).ToArray();
            if (reads.Length != (__originalMethod.Name == "UpdateWalking" ? 6 : 1))
                throw new InvalidOperationException("The native ice-traction checks changed.");
            // Ground AI cannot steer through the player skating model. Retain the established follower grip
            // and give unmounted ground movement the same traction, without changing equipment or player input.
            foreach (var read in reads)
            {
                read.opcode = OpCodes.Call;
                read.operand = AccessTools.Method(typeof(CreatureIceGrip), nameof(HasIceGrip));
            }
            if (__originalMethod.Name != "UpdateWalking") return code;
            // Ice lifts the game's 20 m/s limit on one step's change of velocity so skating can build speed. With grip there is
            // no skating, and without the limit an attack's root motion, such as the Krigen's charge, carries a creature
            // 15-30 m across ice instead of the 3 m it moves on other ground.
            var slipping = AccessTools.Field(typeof(Character), "m_slipping");
            var limits = Enumerable.Range(3, code.Count - 3).Where(index => code[index].LoadsField(slipping) && code[index - 1].IsLdarg(0) &&
                code[index - 2].Branches(out _) && code[index - 3].LoadsConstant(20f)).ToArray();
            if (limits.Length != 1) throw new InvalidOperationException("The native velocity-change limit on ice changed.");
            code[limits[0]].opcode = OpCodes.Call;
            code[limits[0]].operand = AccessTools.Method(typeof(CreatureIceGrip), nameof(Unlimited));
            return code;
        }

        // Whether the 20 m/s velocity-change limit is lifted: only for those who skate.
        private static bool Unlimited(Character character) => Slipping(character) && !HasIceGrip(character);
    }
}
