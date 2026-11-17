using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // A long buried vein can need more than vanilla's three passes to reach its terrain-supported tips.
    // Keep every native contact rule, but propagate until a full pass adds no supported sections.
    [HarmonyPatch(typeof(MineRock5), "UpdateSupport")]
    internal static class OreSupport
    {
        private static readonly FieldInfo Areas = AccessTools.Field(typeof(MineRock5), "m_hitAreas");
        private static readonly FieldInfo Supported = AccessTools.Field(AccessTools.Inner(typeof(MineRock5), "HitArea"), "m_supported");

        private static bool Continue(MineRock5 rock, ref int previous)
        {
            int count = 0;
            foreach (var area in (IEnumerable)Areas.GetValue(rock))
                if ((bool)Supported.GetValue(area)) count++;
            bool changed = count != previous;
            previous = count;
            return changed;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = instructions.ToList();
            int limit = code.FindIndex(instruction => instruction.LoadsConstant(3));
            if (limit < 1 || code.FindLastIndex(instruction => instruction.LoadsConstant(3)) != limit ||
                !code[limit - 1].IsLdloc() || (code[limit + 1].opcode != OpCodes.Blt && code[limit + 1].opcode != OpCodes.Blt_S))
                throw new InvalidOperationException("Valheim's ore support loop changed; expected the three-pass limit.");
            var previous = generator.DeclareLocal(typeof(int));
            code[limit - 1].opcode = OpCodes.Ldarg_0;
            code[limit - 1].operand = null;
            code[limit].opcode = OpCodes.Ldloca;
            code[limit].operand = previous;
            code[limit + 1].opcode = OpCodes.Brtrue;
            code.Insert(limit + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(OreSupport), nameof(Continue))));
            code.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldc_I4_M1), new CodeInstruction(OpCodes.Stloc, previous) });
            return code;
        }
    }
}
