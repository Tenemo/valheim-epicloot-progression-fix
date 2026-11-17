using System;
using System.Collections.Generic;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Swimming reads a fixed speed where running applies its skill factor. Apply the same 25%-at-100 curve
    // to players' Swim skill, including EpicLoot's surplus, before the existing attack and status modifiers.
    [HarmonyPatch(typeof(Character), "UpdateSwimming")]
    internal static class SwimSpeed
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.LoadsField(AccessTools.Field(typeof(Character), nameof(Character.m_swimSpeed))),
                AccessTools.Method(typeof(SwimSpeed), nameof(WithSkill)), 1, "Valheim's swimming speed read changed.");

        internal static float WithSkill(Character character) => character.m_swimSpeed *
            (character.IsPlayer() ? 1f + 0.25f * character.GetSkillFactor(Skills.SkillType.Swim) : 1f);
    }
}
