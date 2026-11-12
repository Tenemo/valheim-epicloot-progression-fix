using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Both jump checks use the unmodified cost, although Player.OnJump pays the equipment, status and world cost.
    [HarmonyPatch]
    internal static class JumpStaminaEligibility
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Character), nameof(Character.Jump));
            yield return AccessTools.Method(typeof(DoubleJump), nameof(DoubleJump.MultiJump));
        }

        private static bool CanJump(Character character, float baseCost)
        {
            if (!(character is Player player)) return character.HaveStamina(baseCost);
            float cost = baseCost - baseCost * player.GetEquipmentMovementModifier() +
                baseCost * player.GetEquipmentJumpStaminaModifier();
            player.GetSEMan().ModifyJumpStaminaUsage(cost, ref cost);
            cost *= Game.m_moveStaminaRate * Game.m_staminaRate;
            // Keep EpicLoot's alternative-resource check for positive costs; a free jump needs no reserve.
            return cost <= 0f || player.HaveStamina(cost);
        }

        private static void LaunchAirJump(Character character, Vector3 velocity, bool effects)
        {
            velocity = SwimmingDoubleJump.PrepareLaunch(character, velocity);
            character.ForceJump(velocity, effects);
            SwimmingDoubleJump.Launched(character);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            bool groundJump = original.DeclaringType == typeof(Character);
            var check = AccessTools.Method(typeof(Character), nameof(Character.HaveStamina));
            var replacement = AccessTools.Method(typeof(JumpStaminaEligibility), nameof(CanJump));
            var launch = AccessTools.Method(typeof(Character), nameof(Character.ForceJump));
            int checks = 0, launches = 0;
            foreach (var instruction in instructions)
            {
                // Character.Jump's second check belongs to grappling; preserve that separate action.
                if (instruction.Calls(check) && checks++ == 0)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
                if (!groundJump && instruction.Calls(launch))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(JumpStaminaEligibility), nameof(LaunchAirJump));
                    launches++;
                }
                yield return instruction;
            }
            // EpicLoot now computes the full air-jump cost and counts only successful launches itself.
            if (checks != (groundJump ? 2 : 0) || (!groundJump && launches != 1))
                throw new InvalidOperationException("Jump methods changed; expected two ground checks and one air-jump launch.");
        }
    }
}
