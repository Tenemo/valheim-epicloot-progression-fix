using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // A player's parry window starts from the synced base instead of the game's fixed 0.25 s; creatures keep the game's.
    // EpicLoot's Parry window enchantment still takes its milliseconds off the block timer before the check, so its bonus
    // adds to the new base. The game's parry, EpicLoot's parry bonuses and Eitr Weave, and StarLevelSystem's parry drain
    // reduction all compare against the same window, and tower shields still cannot parry.
    [HarmonyPatch]
    internal static class ParryWindow
    {
        internal const float NativeSeconds = 0.25f;

        internal static float Seconds(Humanoid humanoid) => humanoid is Player ? Plugin.BaseParryWindow.Value : NativeSeconds;

        // EpicLoot's parry check reads the local player's block timer.
        private static float LocalSeconds() => Seconds(Player.m_localPlayer);

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Humanoid), "BlockAttack");
            yield return AccessTools.Method(typeof(ModifyParryWindow), nameof(ModifyParryWindow.IsParry));
            yield return AccessTools.Method(AccessTools.Inner(typeof(EitrWeaving), "PatchParry_EitrWeave"), "Postfix");
            if (StarLevelSystem.Installed)
                yield return AccessTools.Method(StarLevelSystem.Assembly.GetType("StarLevelSystem.Modifiers.Drainers+ModifierDrainBlock", true), "Postfix");
        }

        // Each has one m_blockTimer < 0.25f comparison: the blocking humanoid's own window replaces the constant.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            var timer = AccessTools.Field(typeof(Humanoid), "m_blockTimer");
            var matches = Enumerable.Range(1, code.Count - 1).Where(index => code[index].opcode == OpCodes.Ldc_R4 &&
                Equals(code[index].operand, NativeSeconds) && code[index - 1].LoadsField(timer)).ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " changed; expected one 0.25 s parry window.");
            var window = code[matches[0]];
            // The blocking humanoid: the instance itself, or a postfix's Humanoid argument.
            int argument = __originalMethod.IsStatic
                ? __originalMethod.GetParameters().FirstOrDefault(parameter => parameter.ParameterType == typeof(Humanoid))?.Position ?? -1 : 0;
            if (argument < 0)
            {
                window.opcode = OpCodes.Call;
                window.operand = AccessTools.Method(typeof(ParryWindow), nameof(LocalSeconds));
                return code;
            }
            window.opcode = new[] { OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2, OpCodes.Ldarg_3 }[argument];
            window.operand = null;
            code.Insert(matches[0] + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ParryWindow), nameof(Seconds))));
            return code;
        }
    }
}
