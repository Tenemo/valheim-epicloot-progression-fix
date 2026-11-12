using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using EpicLoot;
using EpicLoot.ShardStones;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Gift rarity determines strength; equipment rarity still determines its native socket capacity.
    [HarmonyPatch(typeof(ShardSlotChisel), nameof(ShardSlotChisel.CanApply))]
    internal static class GiftSlots
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var rarity = AccessTools.Field(typeof(MagicItem), nameof(MagicItem.Rarity));
            var matches = Enumerable.Range(1, code.Count - 4).Where(index =>
                code[index - 1].LoadsField(rarity) &&
                (code[index].opcode == OpCodes.Bge || code[index].opcode == OpCodes.Bge_S) &&
                code[index + 2].opcode == OpCodes.Ldstr && Equals(code[index + 2].operand, "$mod_epicloot_slotchisel_raritytoolow")).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("EpicLoot's gift rarity restriction changed; expected exactly one comparison.");
            int at = matches[0];
            var destination = code[at].operand;
            code[at].opcode = OpCodes.Pop;
            code[at].operand = null;
            code.InsertRange(at + 1, new[] { new CodeInstruction(OpCodes.Pop), new CodeInstruction(OpCodes.Br, destination) });
            return code;
        }
    }
}
