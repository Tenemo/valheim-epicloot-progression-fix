using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(SE_Cozy), nameof(SE_Cozy.UpdateStatusEffect))]
    internal static class RestedDelay
    {
        private static float EffectiveDelay(float original, SE_Cozy resting)
        {
            return resting.m_statusEffect == "Rested" ? original * Plugin.RestedDelayMultiplier.Value : original;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var delay = AccessTools.Field(typeof(SE_Cozy), nameof(SE_Cozy.m_delay));
            int changes = 0;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode != OpCodes.Ldfld || !Equals(instruction.operand, delay)) continue;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RestedDelay), nameof(EffectiveDelay)));
                changes++;
            }
            if (changes != 1) throw new InvalidOperationException("Valheim's resting delay changed; expected one delay comparison.");
        }
    }
}
