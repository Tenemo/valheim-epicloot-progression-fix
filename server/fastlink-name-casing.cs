using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // FastLink 1.5.3 lowercases character names, mod identifiers and delay units with the current Windows culture.
    // In Turkish, uppercase I then fails to match lowercase i. These identifiers must not depend on the PC's language.
    [HarmonyPatch]
    internal static class FastLinkNameCasing
    {
        private static readonly MethodInfo Lower = AccessTools.Method(typeof(string), nameof(string.ToLower), Type.EmptyTypes);
        private static readonly MethodInfo Invariant = AccessTools.Method(typeof(string), nameof(string.ToLowerInvariant));
        private static bool Prepare() => PluginIds.TryGet(PluginIds.FastLink, out _);
        private static IEnumerable<MethodBase> TargetMethods()
        {
            if (!PluginIds.TryGet(PluginIds.FastLink, out var plugin)) return Array.Empty<MethodBase>();
            var methods = plugin.Instance.GetType().Assembly.GetTypes()
                .Where(type => type.Namespace != null && type.Namespace.StartsWith("FastLink", StringComparison.Ordinal))
                .SelectMany(AccessTools.GetDeclaredMethods)
                .Where(method => method.GetMethodBody() != null && PatchProcessor.GetOriginalInstructions(method).Any(instruction => instruction.Calls(Lower)))
                .Cast<MethodBase>().ToArray();
            if (methods.Length != 13) throw new InvalidOperationException("FastLink's character and mod name comparisons changed.");
            return methods;
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(Lower)) instruction.operand = Invariant;
                yield return instruction;
            }
        }
    }
}
