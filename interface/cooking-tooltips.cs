using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class CookingTooltips
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.BetterUi, out var plugin)) return;
            var hover = plugin.Instance.GetType().Assembly.GetType("BetterUI.Patches.HoverText");
            var cooking = hover == null ? null : AccessTools.Method(hover, "PatchCookingStation");
            if (cooking == null) throw new InvalidOperationException("BetterUI's cooking tooltip moved.");
            harmony.Patch(cooking, transpiler: new HarmonyMethod(typeof(CookingTooltips), nameof(ReadSyncedSlots)));
        }

        // Valheim's station prefabs leave the fully cooked action label empty. BetterUI also uses the native
        // hover text when only charcoal remains, so fill the missing label when a station loads.
        [HarmonyPatch(typeof(CookingStation), "Awake"), HarmonyPostfix]
        private static void RestoreActionLabel(CookingStation __instance)
        {
            if (string.IsNullOrEmpty(__instance.m_fullyCookedTooltip))
                __instance.m_fullyCookedTooltip = "Take item";
        }

        // BetterUI only reads synchronized slots here, but required this client to own the station. Other players lost
        // every timer and, with all food cooked, saw vanilla's empty action labels. Keep the valid-ZDO check only. Its
        // GetSlot(i, out name, out cookedTime, ...) is followed by projecting cookedTime to this second (see CookingProgress).
        private static IEnumerable<CodeInstruction> ReadSyncedSlots(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var isOwner = AccessTools.Method(typeof(ZNetView), nameof(ZNetView.IsOwner));
            var ownership = code.Where(instruction => instruction.Calls(isOwner)).ToList();
            if (ownership.Count != 1) throw new InvalidOperationException("BetterUI's cooking tooltip ownership check changed.");
            ownership[0].operand = AccessTools.Method(typeof(ZNetView), nameof(ZNetView.IsValid));
            var getSlot = AccessTools.Method(typeof(CookingStation), "GetSlot");
            int index = code.FindIndex(instruction => instruction.Calls(getSlot));
            if (index < 6 || code.FindLastIndex(instruction => instruction.Calls(getSlot)) != index || !code[index - 6].IsLdarg(0))
                throw new InvalidOperationException("BetterUI's cooking slot read changed.");
            code.InsertRange(index + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0), code[index - 5].Clone(), code[index - 3].Clone(),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CookingTooltips), nameof(Project))),
            });
            return code;
        }

        private static void Project(CookingStation station, int slot, ref float cookedTime) => cookedTime = CookingProgress.Displayed(station, slot, cookedTime);
    }

    // FedoHud's cooking lines read each slot's recorded cooked time; they show the time projected to this second instead.
    internal static class FedoHudCooking
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.FedoHud, out var plugin)) return;
            var tooltip = plugin.Instance.GetType().Assembly.GetType("FedoHud.CookingStationTooltip");
            var lines = tooltip == null ? null : AccessTools.Method(tooltip, "BuildLines");
            if (lines == null) throw new InvalidOperationException("FedoHud's cooking tooltip moved.");
            harmony.Patch(lines, transpiler: new HarmonyMethod(typeof(FedoHudCooking), nameof(ProjectSlots)));
        }

        // zdo.GetFloat("slot" + i) becomes the projected value for slot i.
        private static IEnumerable<CodeInstruction> ProjectSlots(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var getFloat = AccessTools.Method(typeof(ZDO), nameof(ZDO.GetFloat), new[] { typeof(string), typeof(float) });
            var reads = code.Where(instruction => instruction.Calls(getFloat)).ToList();
            if (reads.Count != 1) throw new InvalidOperationException("FedoHud's cooking slot read changed.");
            reads[0].opcode = OpCodes.Call;
            reads[0].operand = AccessTools.Method(typeof(FedoHudCooking), nameof(Projected));
            code.Insert(code.IndexOf(reads[0]), new CodeInstruction(OpCodes.Ldarg_0));
            return code;
        }

        private static float Projected(ZDO zdo, string key, float fallback, CookingStation station)
        {
            float recorded = zdo.GetFloat(key, fallback);
            return key.StartsWith("slot", StringComparison.Ordinal) && int.TryParse(key.Substring(4), out int slot) ? CookingProgress.Displayed(station, slot, recorded) : recorded;
        }
    }
}
