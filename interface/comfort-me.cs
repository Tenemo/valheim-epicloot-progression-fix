using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Configs;

namespace EpicLootProgressionFix
{
    internal static class ComfortBuildActions
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.ComfortMe, out var plugin)) return;
            var filter = plugin.Instance.GetType().Assembly.GetType("ComfortMe.ComfortUsageTag");
            var fill = filter == null ? null : AccessTools.Method(filter, "Fill", new[] { typeof(PieceTable), typeof(IList<Piece>) });
            if (fill == null) throw new InvalidOperationException("ComfortMe's Comfort category filter moved.");
            harmony.Patch(fill, postfix: new HarmonyMethod(typeof(ComfortBuildActions), nameof(KeepActions)));
        }

        private static void KeepActions(PieceTable table, IList<Piece> resultOut)
        {
            if (!table || table.m_availablePieces == null || resultOut == null) return;
            foreach (var piece in table.m_availablePieces)
                if (piece && (piece.m_repairPiece || piece.m_removePiece) && !resultOut.Contains(piece)) resultOut.Add(piece);
        }
    }

    // ComfortMe 1.2.2 labels a hammer piece's chip with the piece's own comfort, but the game counts only the best piece
    // of each comfort group around the player, so beside a Deer Rug (1) the Bearskin Rug's chip read +2 although building
    // it adds 1. ComfortMe already works out that gain to decide which pieces get a chip; label the chip with it instead.
    internal static class ComfortChipGain
    {
        private static Type classification;
        private static MethodInfo gain;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.ComfortMe, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var badge = assembly.GetType("ComfortMe.BuildHudBadge");
            classification = assembly.GetType("ComfortMe.ComfortClassification");
            var apply = badge == null ? null : AccessTools.Method(badge, "Apply");
            gain = classification == null ? null : AccessTools.PropertyGetter(classification, "Delta");
            if (apply == null || gain == null) throw new InvalidOperationException("ComfortMe's chip moved.");
            harmony.Patch(apply, transpiler: new HarmonyMethod(typeof(ComfortChipGain), nameof(LabelWithGain)));
            log.LogInfo("ComfortMe's hammer chips show how much each piece would raise comfort.");
        }

        // The label is "+" + piece.m_comfort, piece being Apply's second argument; read the classification's gain instead.
        private static IEnumerable<CodeInstruction> LabelWithGain(IEnumerable<CodeInstruction> instructions)
        {
            var comfort = AccessTools.Field(typeof(Piece), nameof(Piece.m_comfort));
            var text = AccessTools.Method(typeof(int), nameof(int.ToString), Type.EmptyTypes);
            var code = instructions.ToList();
            var labels = Enumerable.Range(1, code.Count - 2).Where(index => code[index - 1].IsLdarg(1)
                && code[index].LoadsField(comfort, byAddress: true) && code[index + 1].Calls(text)).ToList();
            var address = code.FirstOrDefault(instruction => (instruction.opcode == OpCodes.Ldloca_S || instruction.opcode == OpCodes.Ldloca)
                && instruction.operand is LocalBuilder local && local.LocalType == classification);
            if (labels.Count != 1 || address == null) throw new InvalidOperationException("ComfortMe's chip label changed.");
            int label = labels[0];
            code[label - 1].opcode = address.opcode;
            code[label - 1].operand = address.operand;
            code[label].opcode = OpCodes.Call;
            code[label].operand = gain;
            code[label + 1].operand = AccessTools.Method(typeof(ComfortChipGain), nameof(Gain));
            return code;
        }

        // With BadgeUpgradesOnly off, ComfortMe also marks pieces that would add nothing; those read +0.
        private static string Gain(int delta) => Math.Max(delta, 0).ToString();
    }

    [HarmonyPatch(typeof(Piece), nameof(Piece.GetAllComfortPiecesInRadius))]
    internal static class ComfortRadius
    {
        // Rested and ComfortMe share this search, so the room panel and upgrade chips use the same range.
        private static void Prefix(ref float radius) => radius = Plugin.ComfortReach.Value;
    }

    // ComfortMe 1.2.2 shows its "This room" panel whenever the player is in place mode, which every build tool turns on:
    // the hoe, the cultivator and the serving tray as well as the hammer. Only the hammer builds comfort pieces, so show
    // the panel only while the hammer is out.
    internal static class ComfortRoomPanel
    {
        private static AccessTools.FieldRef<Player, PieceTable> placeModePieces;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.ComfortMe, out var plugin)) return;
            var hud = plugin.Instance.GetType().Assembly.GetType("ComfortMe.HudUi");
            var holdingPlaceTool = hud == null ? null : AccessTools.Method(hud, "HoldingPlaceTool", Type.EmptyTypes);
            if (holdingPlaceTool == null || !holdingPlaceTool.IsStatic || holdingPlaceTool.ReturnType != typeof(bool))
                throw new InvalidOperationException("ComfortMe's build tool check moved.");
            // The piece table of the build tool in hand; place mode is on while it is set.
            placeModePieces = AccessTools.FieldRefAccess<Player, PieceTable>("m_buildPieces");
            harmony.Patch(holdingPlaceTool, postfix: new HarmonyMethod(typeof(ComfortRoomPanel), nameof(OnlyWithHammer)));
            log.LogInfo("ComfortMe's This room panel shows only with the hammer.");
        }

        private static void OnlyWithHammer(ref bool __result)
        {
            if (!__result) return;
            var player = Player.m_localPlayer;
            var pieces = player != null ? placeModePieces(player) : null;
            __result = pieces != null && pieces.name == PieceTables.Hammer;
        }
    }
}
