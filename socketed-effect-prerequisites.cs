using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EpicLoot;
using EpicLoot.ShardStones;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class SocketedEffectPrerequisites
    {
        private static readonly MethodInfo Requirements = AccessTools.Method(typeof(MagicItemEffectRequirements),
            nameof(MagicItemEffectRequirements.CheckRequirements), new[] { typeof(ItemDrop.ItemData), typeof(MagicItem),
                typeof(RequirementFailure).MakeByRefType(), typeof(string).MakeByRefType(), typeof(string),
                typeof(bool), typeof(bool), typeof(bool), typeof(bool) });

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return Requirements;
            yield return AccessTools.Method(typeof(ShardSocketManager), nameof(ShardSocketManager.CanSocket));
            yield return AccessTools.Method(typeof(ShardSocketManager), nameof(ShardSocketManager.CanCoexist));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            var requiredTypes = AccessTools.Field(typeof(MagicItemEffectRequirements), nameof(MagicItemEffectRequirements.MustHaveEffectTypes));
            var hasEffect = AccessTools.Method(typeof(MagicItem), nameof(MagicItem.HasEffect));
            bool prerequisites = false;
            int replacements = 0;
            for (int index = 0; index < code.Count; index++)
            {
                var instruction = code[index];
                if (__originalMethod.DeclaringType == typeof(MagicItemEffectRequirements))
                {
                    if (instruction.LoadsField(requiredTypes)) prerequisites = true;
                    if (!prerequisites || !instruction.Calls(hasEffect)) continue;
                    // Only the prerequisite lookup includes sockets; native duplicate and conflict checks keep their own rules.
                    if (index == 0 || code[index - 1].opcode != OpCodes.Ldc_I4_0)
                        throw new InvalidOperationException("Expected a rolled-only prerequisite lookup.");
                    code[index - 1].opcode = OpCodes.Ldc_I4_1;
                }
                else
                {
                    if (!instruction.Calls(Requirements)) continue;
                    // A swap checks the occupants that remain; filling an empty socket checks the item as it is.
                    bool swap = __originalMethod.Name == nameof(ShardSocketManager.CanCoexist);
                    if (swap)
                    {
                        var residents = new CodeInstruction(OpCodes.Ldarg_2);
                        residents.labels.AddRange(instruction.labels);
                        instruction.labels.Clear();
                        code.Insert(index++, residents);
                    }
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(SocketedEffectPrerequisites), swap ? nameof(CheckRemainingSockets) : nameof(CheckSocket));
                }
                replacements++;
            }
            if (replacements != 1)
                throw new InvalidOperationException("Expected one socket prerequisite check in " + __originalMethod.Name);
            return code;
        }

        private static bool CheckRemainingSockets(MagicItemEffectRequirements requirements, ItemDrop.ItemData item, MagicItem magic,
            out RequirementFailure failure, out string conflict, string effectType, bool lootRoll, bool augmentRoll,
            bool runeRoll, bool itemTypeGating, IEnumerable<ItemDrop.ItemData> residents)
        {
            var remaining = new List<SocketedEffect>();
            foreach (var resident in residents)
                if (ShardSocketManager.ResolveSocketedEffect(item, resident, out var effect, out _, out _) && effect != null)
                    remaining.Add(new SocketedEffect { Effect = effect });
            var original = magic.Sockets;
            magic.Sockets = remaining;
            try
            {
                return CheckSocket(requirements, item, magic, out failure, out conflict, effectType, lootRoll, augmentRoll, runeRoll, itemTypeGating);
            }
            finally { magic.Sockets = original; }
        }

        // EpicLoot checks a socket's exclusions against the rolled affixes only. An Indestructible rune leaves out the rolled
        // durability bonus it overrides; its other requirements, including its Legendary minimum, still apply.
        private static bool CheckSocket(MagicItemEffectRequirements requirements, ItemDrop.ItemData item, MagicItem magic,
            out RequirementFailure failure, out string conflict, string effectType, bool lootRoll, bool augmentRoll,
            bool runeRoll, bool itemTypeGating)
        {
            var original = magic.Effects;
            magic.Effects = SocketConflicts.RolledBeside(magic, effectType).ToList();
            try
            {
                return requirements.CheckRequirements(item, magic, out failure, out conflict, effectType,
                    lootRoll, augmentRoll, runeRoll, itemTypeGating);
            }
            finally { magic.Effects = original; }
        }
    }
}
