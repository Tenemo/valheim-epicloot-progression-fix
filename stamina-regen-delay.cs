using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Player), "RPC_UseStamina")]
    internal static class StaminaRegenDelay
    {
        internal static float EffectiveDelay(float original, Player player)
        {
            if (player != Player.m_localPlayer) return original;
            float bonus = ModifyPlayerRegen.GetModifyRegenValue(player, MagicEffectType.ModifyStaminaRegen);
            return original * (1f - Plugin.StaminaRegenDelayReduction.Value * Mathf.Clamp01(bonus));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var delay = AccessTools.Field(typeof(Player), nameof(Player.m_staminaRegenDelay));
            int changes = 0;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode != OpCodes.Ldfld || !Equals(instruction.operand, delay)) continue;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(StaminaRegenDelay), nameof(EffectiveDelay)));
                changes++;
            }
            if (changes != 1) throw new InvalidOperationException("Valheim's stamina regeneration delay changed; expected one timer assignment.");
        }
    }
}
