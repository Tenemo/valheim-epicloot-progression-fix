using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class ArmorDisplay
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.BetterUi, out var plugin)) return;
            var inventory = plugin.Instance.GetType().Assembly.GetType("BetterUI.GameClasses.BetterInventoryGui");
            var update = inventory == null ? null : AccessTools.Method(inventory, "PatchArmor");
            if (update == null) throw new InvalidOperationException("BetterUI's inventory armor display moved.");
            harmony.Patch(update, transpiler: new HarmonyMethod(typeof(ArmorDisplay), nameof(FormatArmor)));
        }

        // BetterUI overwrites EpicLoot's formatted armor label with the raw float each inventory update.
        // Format that conversion only; the armor value used for combat keeps its full precision.
        private static IEnumerable<CodeInstruction> FormatArmor(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var raw = AccessTools.Method(typeof(float), nameof(float.ToString), Type.EmptyTypes);
            var matches = code.Where(instruction => instruction.Calls(raw)).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("BetterUI's armor number conversion changed.");
            var conversion = matches[0];
            code.Insert(code.IndexOf(conversion), new CodeInstruction(OpCodes.Ldstr, "0.##").MoveLabelsFrom(conversion));
            conversion.operand = AccessTools.Method(typeof(float), nameof(float.ToString), new[] { typeof(string) });
            return code;
        }
    }
}
