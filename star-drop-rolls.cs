using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class StarDropRolls
    {
        private static object dropInstances;
        private static MethodInfo findDrop;
        private static PropertyInfo template;
        private static PropertyInfo conditions;
        private static PropertyInfo minimumLevel;
        private static PropertyInfo maximumLevel;

        internal static void Initialize()
        {
            if (!PluginIds.TryGet(PluginIds.DropThat, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            const string prefix = "DropThat.Drop.CharacterDropSystem.";
            var manager = assembly.GetType(prefix + "Managers.CharacterDropSessionManager");
            dropInstances = manager?.GetProperty("DropInstanceTable")?.GetValue(null);
            findDrop = dropInstances?.GetType().GetMethod("TryGetValue");
            template = assembly.GetType(prefix + "Models.DropConfigInfo")?.GetProperty("DropTemplate");
            conditions = assembly.GetType(prefix + "Models.CharacterDropDropTemplate")?.GetProperty("Conditions");
            minimumLevel = assembly.GetType(prefix + "Conditions.ConditionLevelMin")?.GetProperty("MinLevel");
            maximumLevel = assembly.GetType(prefix + "Conditions.ConditionLevelMax")?.GetProperty("MaxLevel");
            if (findDrop == null || template == null || conditions == null || minimumLevel == null || maximumLevel == null)
                throw new InvalidOperationException("Unsupported Drop That API: cannot recognize level-conditioned drop chances.");
        }

        // This changes only the decision to use vanilla's countdown. The existing chance roll,
        // level modifiers, drop conditions and amount calculation still run in their original order.
        internal static bool UseIndependentRoll(bool globallyDisabled, CharacterDrop.Drop drop)
        {
            if (globallyDisabled || drop.m_levelMultiplier) return true;
            if (dropInstances == null) return false;
            var arguments = new object[] { drop, null };
            if (!(bool)findDrop.Invoke(dropInstances, arguments)) return false;
            var settings = template.GetValue(arguments[1]);
            if (settings == null || !(conditions.GetValue(settings) is IEnumerable rules)) return false;
            foreach (var rule in rules)
            {
                if (minimumLevel.DeclaringType.IsInstanceOfType(rule) && minimumLevel.GetValue(rule) is int min && min >= 0)
                    return true;
                if (maximumLevel.DeclaringType.IsInstanceOfType(rule) && maximumLevel.GetValue(rule) is int max && max >= 0)
                    return true;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
    internal static class IndependentStarDropChances
    {
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            var drop = __originalMethod.GetMethodBody().LocalVariables.Single(x => x.LocalType == typeof(CharacterDrop.Drop));
            var globalKey = AccessTools.Method(typeof(ZoneSystem), "GetGlobalKey", new[] { typeof(GlobalKeys) });
            int changes = 0;
            for (int index = 0; index < code.Count; index++)
            {
                var instruction = code[index];
                yield return instruction;
                if (!instruction.Calls(globalKey) || index == 0 || !code[index - 1].LoadsConstant((long)GlobalKeys.NoPseudoDrops)) continue;
                yield return new CodeInstruction(OpCodes.Ldloc, drop.LocalIndex);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(StarDropRolls), nameof(StarDropRolls.UseIndependentRoll)));
                changes++;
            }
            if (changes != 1) throw new InvalidOperationException("Valheim's pseudo-drop gate changed; expected one NoPseudoDrops check.");
        }
    }
}
