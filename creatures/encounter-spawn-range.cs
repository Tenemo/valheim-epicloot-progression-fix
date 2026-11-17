using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(CreatureSpawner), "UpdateSpawner")]
    internal static class EncounterSpawnRange
    {
        // Loading and ownership already follow the selected simulation distance, but outdoor encounter spawners still
        // waited for their native 60 m trigger. Their next five-second tick could then put creatures beside a runner.
        // Only the ordinary proximity trigger changes; the separate noise trigger and dungeon interiors keep theirs.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(Player), nameof(Player.IsPlayerInRange),
                new[] { typeof(Vector3), typeof(float) })), AccessTools.Method(typeof(EncounterSpawnRange), nameof(PlayerInRange)), 1,
                "The encounter spawner's proximity trigger changed.");

        private static bool PlayerInRange(Vector3 point, float nativeRange) => Player.IsPlayerInRange(point,
            point.y > DungeonInteriors.InteriorHeight ? nativeRange : Mathf.Max(nativeRange, SimulationPresets.LocalRadius));
    }
}
