using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class FishWater
    {
        private static readonly AccessTools.FieldRef<Fish, WaterVolume> Water = AccessTools.FieldRefAccess<Fish, WaterVolume>("m_waterVolume");
        private static readonly AccessTools.FieldRef<Fish, float> CurrentWave = AccessTools.FieldRefAccess<Fish, float>("m_waterWave");
        private static readonly AccessTools.FieldRef<Fish, float> LastWave = AccessTools.FieldRefAccess<Fish, float>("m_lastWave");
        private static readonly MethodInfo CalculateWave = AccessTools.Method(typeof(WaterVolume), nameof(WaterVolume.CalcWave),
            new[] { typeof(Vector3), typeof(float), typeof(float), typeof(float), typeof(float) });

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Fish), "Start");
            yield return AccessTools.Method(typeof(Fish), nameof(Fish.CustomFixedUpdate));
        }

        private static void Prefix(Fish __instance)
        {
            var water = Water(__instance);
            if (water && !water.m_useGlobalWind) CurrentWave(__instance) = LastWave(__instance) = 0f;
        }

        private static float Wave(WaterVolume water, Vector3 position, float depth, float time, float factor, float bigFactor) =>
            water.m_useGlobalWind ? water.CalcWave(position, depth, time, factor, bigFactor) : 0f;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(CalculateWave),
                AccessTools.Method(typeof(FishWater), nameof(Wave)), 1, "Fish wave calculation changed; expected one native wave sample.");
    }
}
