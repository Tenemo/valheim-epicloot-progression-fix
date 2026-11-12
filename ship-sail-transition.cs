using System.Collections.Generic;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Sail animation runs on the dedicated server too. Native effect attribution dereferences its absent local
    // player before buoyancy runs, aborting every physics update until the sail reaches its new position.
    [HarmonyPatch(typeof(Ship), "UpdateSailSize")]
    internal static class ShipSailTransition
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var playerId = AccessTools.Method(typeof(Player), nameof(Player.GetPlayerID));
            var networkId = AccessTools.Method(typeof(Character), nameof(Character.GetZDOID));
            var code = Transpilers.CallInstead(instructions, instruction => instruction.Calls(playerId),
                AccessTools.Method(typeof(ShipSailTransition), nameof(PlayerId)), 1, "The ship sail effect's player attribution changed.");
            return Transpilers.CallInstead(code, instruction => instruction.Calls(networkId),
                AccessTools.Method(typeof(ShipSailTransition), nameof(NetworkId)), 1, "The ship sail effect's network attribution changed.");
        }

        private static long PlayerId(Player player) => player ? player.GetPlayerID() : 0L;
        private static ZDOID NetworkId(Character character) => character ? character.GetZDOID() : ZDOID.None;
    }
}
