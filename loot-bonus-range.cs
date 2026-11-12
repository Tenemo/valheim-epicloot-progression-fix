using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EpicLoot;
using EpicLoot.MagicItemEffects.Shards;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class LootBonusRange
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(LootRoller), nameof(LootRoller.GetLuckFactor));
            yield return AccessTools.Method(typeof(LuckyLoot), "BestNearbyValue");
            yield return AccessTools.Method(typeof(global::EpicLoot.Magic.MagicItemEffects.Prosperity), "GetFactor");
        }

        internal static float Value() => Plugin.LootBonusRadius.Value;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var matches = Enumerable.Range(0, code.Count - 2).Where(index => code[index].opcode == OpCodes.Ldc_R4 &&
                Equals(code[index].operand, 100f) && code[index + 2].Calls(AccessTools.Method(typeof(Player), nameof(Player.GetPlayersInRange)))).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("EpicLoot's loot player lookup changed; expected one 100 m radius.");
            var radius = code[matches[0]];
            radius.opcode = OpCodes.Call;
            radius.operand = AccessTools.Method(typeof(LootBonusRange), nameof(Value));
            return code;
        }
    }
}
