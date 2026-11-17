using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class AttachedCartMaterials
    {
        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var plugin)) return;
            var boxes = plugin.Instance.GetType().Assembly.GetType("AzuCraftyBoxes.Util.Functions.Boxes");
            // Reference types share one compiled body, so this patch must target the instantiation helm simulation and chest supply patch, or they replace each other.
            var nearby = boxes == null ? null : AccessTools.Method(boxes, "GetNearbyContainers")?.MakeGenericMethod(typeof(UnityEngine.Component));
            if (nearby == null) throw new InvalidOperationException("AzuCraftyBoxes' cart material search moved.");
            harmony.Patch(nearby, transpiler: new HarmonyMethod(typeof(AttachedCartMaterials), nameof(Transpiler)));
            log.LogInfo("AzuCraftyBoxes includes the locally attached cart while retaining its other material access rules.");
        }

        private static List<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(Vagon), nameof(Vagon.InUse))),
                AccessTools.Method(typeof(AttachedCartMaterials), nameof(Unavailable)), 1, "AzuCraftyBoxes' cart exclusion changed.");

        private static bool Unavailable(Vagon cart)
        {
            var player = Player.m_localPlayer;
            return cart.InUse() && (player == null || !cart.IsAttached(player));
        }
    }
}
