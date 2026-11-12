using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Valheim ignores the keys that close the inventory while a craft runs, and AAA_Crafting 2.1.12 starts each craft of
    // a batch in the frame the previous one ends, so no key closed the window before the whole batch had finished. The
    // keys now work during a craft as between crafts; closing the window cancels the craft in progress and the batch.
    [HarmonyPatch(typeof(InventoryGui), "Update")]
    internal static class CraftingCloseKeys
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var craftTimer = AccessTools.Field(typeof(InventoryGui), "m_craftTimer");
            int index = code.FindIndex(x => x.LoadsField(craftTimer));
            // Valheim 1.0.15: if (m_craftTimer < 0f && no chat, console or menu ...) { panel, close and open keys }
            if (index < 1 || code.FindLastIndex(x => x.LoadsField(craftTimer)) != index || index + 2 >= code.Count ||
                !code[index - 1].IsLdarg(0) || !code[index + 1].Is(OpCodes.Ldc_R4, 0f) ||
                (code[index + 2].opcode != OpCodes.Bge_Un && code[index + 2].opcode != OpCodes.Bge_Un_S))
                throw new InvalidOperationException("Valheim's inventory keys changed; expected one crafting check.");
            // The checks before this one jump to its first instruction, so its labels stay.
            for (int i = index - 1; i <= index + 2; i++)
            {
                code[i].opcode = OpCodes.Nop;
                code[i].operand = null;
            }
            return code;
        }
    }
}
