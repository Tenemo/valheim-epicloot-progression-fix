using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(PlayerController), "FixedUpdate")]
    internal static class KeyboardJumpInput
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var held = AccessTools.Field(typeof(PlayerController), "m_lastJump");
            var pressed = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetButtonDown));
            int matches = 0;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (!instruction.LoadsField(held)) continue;
                // A release and new press can both arrive between physics samples. The buffered press must
                // clear the previous held-state gate; a continuous hold still passes through that gate.
                yield return new CodeInstruction(OpCodes.Ldstr, "Jump");
                yield return new CodeInstruction(OpCodes.Call, pressed);
                yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                yield return new CodeInstruction(OpCodes.Ceq);
                yield return new CodeInstruction(OpCodes.And);
                matches++;
            }
            if (matches != 1)
                throw new InvalidOperationException("Keyboard jump input changed; expected one previous held-state check.");
        }
    }
}
