using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Character), "UpdateWalking")]
    internal static class AirborneIceMovement
    {
        private static bool OnSurface(bool slippery, Character character) =>
            slippery && (!character.IsPlayer() || character.IsOnGround());

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var slipping = AccessTools.Field(typeof(Character), "m_slipping");
            var stores = code.Select((instruction, index) => (instruction, index))
                .Where(entry => entry.instruction.StoresField(slipping)).ToArray();
            if (stores.Length != 1) throw new InvalidOperationException("Character's walking slipping-state assignment changed.");
            int index = stores[0].index;
            var character = new CodeInstruction(OpCodes.Ldarg_0);
            // Any branch into the assignment must also pass through the grounded check.
            character.labels.AddRange(code[index].labels); code[index].labels.Clear();
            code.InsertRange(index, new[] { character, new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(AirborneIceMovement), nameof(OnSurface))) });
            return code;
        }
    }
}
