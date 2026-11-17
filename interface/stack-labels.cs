using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // A full four-digit stack's label, 1000/1000, is wider than its inventory cell, so neighboring labels ran into each
    // other. A stack label of nine or more characters now breaks after its slash into two lines. The game writes the label
    // in every bag and chest cell, and MultiUserChest writes it for a chest's pending change; both now write it wrapped,
    // so it is set once rather than rewritten every frame. Labels of up to eight characters, such as 999/1000, stay as
    // they are.
    internal static class StackLabels
    {
        private const int WrapLength = 9;

        internal static void Initialize(IntegrationPatches harmony)
        {
            var transpiler = new HarmonyMethod(typeof(StackLabels), nameof(WrapFormatted));
            harmony.Patch(AccessTools.Method(typeof(InventoryGrid), "UpdateGui"), transpiler: transpiler);
            if (!PluginIds.TryGet(PluginIds.MultiUserChest, out var chests)) return;
            var preview = AccessTools.Method(chests.Instance.GetType().Assembly.GetType("MultiUserChest.Patches.InventoryGuiPatch", true), "ShowItem");
            if (preview == null) throw new InvalidOperationException("MultiUserChest's pending-change labels moved.");
            harmony.Patch(preview, transpiler: transpiler);
        }

        private static string Wrap(string label)
        {
            int slash = label.IndexOf('/');
            return label.Length < WrapLength || slash < 0 ? label : label.Insert(slash + 1, "\n");
        }

        private static IEnumerable<CodeInstruction> WrapFormatted(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var format = AccessTools.Method(typeof(string), nameof(string.Format), new[] { typeof(string), typeof(object), typeof(object) });
            var codes = instructions.ToList();
            int label = codes.FindIndex(code => code.opcode == OpCodes.Ldstr && code.operand as string == "{0}/{1}");
            int formatted = label < 0 ? -1 : codes.FindIndex(label, code => code.Calls(format));
            if (formatted < 0) throw new InvalidOperationException($"{original.DeclaringType.Name}.{original.Name} no longer formats a stack label.");
            codes.Insert(formatted + 1, CodeInstruction.Call(typeof(StackLabels), nameof(Wrap)));
            return codes;
        }
    }
}
