using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class Transpilers
    {
        // Turns the instructions that match into calls of the replacement, a static method that takes what each of them
        // took from the stack and leaves what it left. Any other number of matches than expected means the game or mod
        // changed, so the patch fails at startup with the given reason.
        internal static List<CodeInstruction> CallInstead(IEnumerable<CodeInstruction> instructions, Func<CodeInstruction, bool> match,
            MethodInfo replacement, int expected, string changed)
        {
            var code = instructions.ToList();
            var matches = code.Where(match).ToList();
            if (matches.Count != expected) throw new InvalidOperationException(changed);
            foreach (var instruction in matches)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
            }
            return code;
        }

        // Replaces the method's one m_nview.IsOwner() check with the given check, which also receives the instance if asked.
        internal static IEnumerable<CodeInstruction> ReplaceOwnerCheck(IEnumerable<CodeInstruction> instructions, Type type, MethodInfo check, bool passInstance)
        {
            var code = instructions.ToList();
            var isOwner = AccessTools.Method(typeof(ZNetView), nameof(ZNetView.IsOwner));
            int index = code.FindIndex(x => x.Calls(isOwner));
            // Valheim 1.0.15: ... if (m_nview.IsOwner()) ...
            if (index < 2 || code.FindLastIndex(x => x.Calls(isOwner)) != index || !code[index - 2].IsLdarg(0) ||
                !code[index - 1].LoadsField(AccessTools.Field(type, "m_nview")) || index + 1 >= code.Count || !code[index + 1].Branches(out _))
                throw new InvalidOperationException($"Valheim's {type.Name} update changed; expected one owner check.");
            code[index].opcode = OpCodes.Call;
            code[index].operand = check;
            if (passInstance) code.Insert(index, new CodeInstruction(OpCodes.Ldarg_0));
            return code;
        }
    }
}
