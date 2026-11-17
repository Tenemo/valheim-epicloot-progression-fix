using System.Collections.Generic;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // A newly built piece's own placement work runs right after the game's placement callback, whether or not Sailing is
    // installed.
    internal static class PiecePlacement
    {
        internal static void Initialize(IntegrationPatches harmony) =>
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.PlacePiece)),
                transpiler: new HarmonyMethod(typeof(PiecePlacement), nameof(Transpiler)));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(AccessTools.Method(typeof(WearNTear), nameof(WearNTear.OnPlaced))),
                AccessTools.Method(typeof(PiecePlacement), nameof(OnPlaced)), 1, "Valheim's piece placement callback changed.");

        private static void OnPlaced(WearNTear piece)
        {
            piece.OnPlaced();
            CraftingCartHealth.OnPlaced(piece);
            SailingShipHealth.OnPlaced(piece);
        }
    }
}
