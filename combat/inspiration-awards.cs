using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using EpicLoot;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Calculate ordinary XP modifiers once, then let EpicLoot allocate that budget across level boundaries.
    // Its raw-XP guard prevents double application inside the allocator; this guard independently prevents procs
    // during the complete award, including the status-effect calculation that precedes raw allocation.
    internal static class InspirationAwards
    {
        private delegate void SkillModifier(Skills skills, ref float factor);
        private delegate void TypedSkillModifier(Skills skills, Skills.SkillType type, ref float factor);
        private delegate void BlockSkillModifier(Skills.SkillType type, ref float factor);
        private static SkillModifier quickLearner, heavyGear;
        private static TypedSkillModifier harvesting;
        private static BlockSkillModifier blocking;
        private static Func<Skills.Skill, float> fortifyMultiplier;
        private static Func<int> maximumLevelUps;
        private static bool granting;
        private static float allocationSkillMultiplier = 1f;
        private static Skills.Skill allocationSkill;
        private static float maximumAwardLevel;

        internal static void Initialize(IntegrationPatches harmony)
        {
            MethodInfo Nested(string effect, string nested, string method = "Prefix") =>
                AccessTools.Method(AccessTools.Inner(ResourcePools.Effect(effect), nested), method);
            quickLearner = AccessTools.MethodDelegate<SkillModifier>(AccessTools.Method(ResourcePools.Effect("QuickLearner_Skills_RaiseSkill_Patch"), "Prefix"));
            heavyGear = AccessTools.MethodDelegate<SkillModifier>(Nested("IncreaseXPGainFromMovementPenalty", "RaiseSkill_Patch"));
            harvesting = AccessTools.MethodDelegate<TypedSkillModifier>(Nested("IncreaseHarvestXPGain", "RaiseSkill_Patch"));
            blocking = AccessTools.MethodDelegate<BlockSkillModifier>(AccessTools.Method(ResourcePools.Effect("DayNightBlockXP_Patch"), "GainDayNightXPOnBlock"));
            var inspiration = ResourcePools.Effect("Inspiration");
            maximumLevelUps = AccessTools.MethodDelegate<Func<int>>(AccessTools.Method(inspiration, "GetMaxLevelUpsPerProc"));
            if (PluginIds.TryGet(PluginIds.FortifySkills, out var fortify))
                fortifyMultiplier = AccessTools.MethodDelegate<Func<Skills.Skill, float>>(
                    AccessTools.Method(fortify.Instance.GetType(), "GetActiveSkillXPMult"), fortify.Instance);
            harmony.Patch(Nested("Inspiration", "RaiseSkill_Patch", "Postfix"),
                prefix: new HarmonyMethod(typeof(InspirationAwards), nameof(AllowProc)));
            harmony.Patch(AccessTools.Method(inspiration, "Grant"),
                prefix: new HarmonyMethod(typeof(InspirationAwards), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(InspirationAwards), nameof(End)),
                transpiler: new HarmonyMethod(typeof(InspirationAwards), nameof(Allocation)));
        }

        private static bool AllowProc() => !granting;

        private static void Begin(Skills skills, Skills.Skill skill, ref float points, out (bool Granting, float Multiplier, Skills.Skill Skill, float Limit) __state)
        {
            __state = (granting, allocationSkillMultiplier, allocationSkill, maximumAwardLevel);
            granting = true;
            allocationSkill = skill;
            maximumAwardLevel = skill.m_level + maximumLevelUps();
            float factor = 1f;
            Player.m_localPlayer.GetSEMan().ModifyRaiseSkill(skill.m_info.m_skill, ref factor);
            quickLearner(skills, ref factor);
            heavyGear(skills, ref factor);
            harvesting(skills, skill.m_info.m_skill, ref factor);
            blocking(skill.m_info.m_skill, ref factor);
            allocationSkillMultiplier = fortifyMultiplier?.Invoke(skill) ?? 1f;
            points = Mathf.Max(0f, points * factor * Game.m_skillGainRate * allocationSkillMultiplier);
        }

        private static Exception End((bool Granting, float Multiplier, Skills.Skill Skill, float Limit) __state, Exception __exception)
        {
            granting = __state.Granting; allocationSkillMultiplier = __state.Multiplier;
            allocationSkill = __state.Skill; maximumAwardLevel = __state.Limit;
            return __exception;
        }

        // EpicLoot's final remainder call otherwise awards another level after its allocation loop reaches the cap.
        private static void RaiseWithinCap(Skills skills, Skills.SkillType type, float factor)
        {
            if (allocationSkill.m_level < maximumAwardLevel) skills.RaiseSkill(type, factor);
        }

        // Fortify's active multiplier runs inside Skill.Raise, after its separate fortified-XP calculation. Normalize
        // the allocator's input by that multiplier so both active and fortified XP receive their ordinary scaling once.
        private static float AllocationWorldRate() => Game.m_skillGainRate * allocationSkillMultiplier;
        private static IEnumerable<CodeInstruction> Allocation(IEnumerable<CodeInstruction> instructions)
        {
            var code = Transpilers.CallInstead(instructions, instruction => instruction.LoadsField(AccessTools.Field(typeof(Game), nameof(Game.m_skillGainRate))),
                AccessTools.Method(typeof(InspirationAwards), nameof(AllocationWorldRate)), 1, "EpicLoot's Inspiration allocator changed.");
            return Transpilers.CallInstead(code, instruction => instruction.Calls(AccessTools.Method(typeof(Skills), nameof(Skills.RaiseSkill))),
                AccessTools.Method(typeof(InspirationAwards), nameof(RaiseWithinCap)), 2, "EpicLoot's Inspiration skill awards changed.");
        }
    }
}
