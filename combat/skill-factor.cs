using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Most skill effects read the skill factor, the level divided by 100. The game caps it at 1, which threw away the
    // levels that set bonuses, trinkets and powers add above 100, while EpicLoot adds its own bonus after the cap. Keep
    // the whole level instead, so a bonus level above 100 works the same wherever it comes from. Effects that cap the
    // factor themselves, such as draw time and stamina drains, still stop at 100.
    [HarmonyPatch(typeof(Skills), nameof(Skills.GetSkillFactor))]
    internal static class SkillFactor
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp01))),
                AccessTools.Method(typeof(SkillFactor), nameof(NotNegative)), 1, "Valheim's skill factor changed.");

        private static float NotNegative(float factor) => Mathf.Max(0f, factor);
    }
}
