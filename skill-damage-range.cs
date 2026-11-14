using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A player's skill sets the roll every attack's damage is multiplied by: EpicLoot rolls between 15% below and 15%
    // above a center that rises from 40% at skill 0 to 100% at skill 100 and continues with the whole skill factor
    // (SkillFactor), so each level above 100 adds 0.6% to the center.
    [HarmonyPatch(typeof(Skills_GetRandomSkillRange_Patch), nameof(Skills_GetRandomSkillRange_Patch.Prefix))]
    internal static class SkillDamageRange
    {
        // The skills whose level can set a damage roll: weapons, and the tools' Woodcutting and Pickaxes hits.
        internal static readonly HashSet<Skills.SkillType> DamageSkills = new HashSet<Skills.SkillType>
        {
            Skills.SkillType.Swords, Skills.SkillType.Knives, Skills.SkillType.Clubs, Skills.SkillType.Polearms,
            Skills.SkillType.Spears, Skills.SkillType.Axes, Skills.SkillType.Bows, Skills.SkillType.Crossbows,
            Skills.SkillType.Unarmed, Skills.SkillType.ElementalMagic, Skills.SkillType.BloodMagic,
            Skills.SkillType.Pickaxes, Skills.SkillType.WoodCutting,
        };

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var skillFactor = AccessTools.Method(typeof(Skills), nameof(Skills.GetSkillFactor));
            var lerp = AccessTools.Method(typeof(Mathf), nameof(Mathf.LerpUnclamped), new[] { typeof(float), typeof(float), typeof(float) });
            var code = instructions.ToList();
            var factors = code.Where(instruction => instruction.Calls(skillFactor)).ToList();
            var lerps = code.Where(instruction => instruction.Calls(lerp)).ToList();
            if (factors.Count != 1 || lerps.Count != 1) throw new InvalidOperationException("EpicLoot's skill damage range changed.");
            factors[0].opcode = OpCodes.Call;
            factors[0].operand = AccessTools.Method(typeof(SkillDamageRange), nameof(DamageFactor));
            return code;
        }

        internal static float DamageFactor(Skills skills, Skills.SkillType skill)
        {
            float factor = skills.GetSkillFactor(skill);
            return AreaSkillRoll.Repeated ? Mathf.Min(factor, 1f) : factor;
        }
    }

    // An area effect set up with an attack's hit, such as an explosion that deals the damage of the projectile that set
    // it off, copies damage the attack already rolled and rolls the skill again, as the game always has. Keep that
    // second roll at its old ceiling, so the level above 100 counts once per hit instead of compounding.
    [HarmonyPatch]
    internal static class AreaSkillRoll
    {
        private static readonly object Copied = new object();
        private static readonly ConditionalWeakTable<Aoe, object> CopiedHits = new ConditionalWeakTable<Aoe, object>();
        internal static bool Repeated;

        [HarmonyPatch(typeof(Aoe), nameof(Aoe.Setup)), HarmonyPostfix]
        private static void RememberHit(Aoe __instance, HitData hitData)
        {
            CopiedHits.Remove(__instance);
            // Setup copies an attack's hit only into an effect that uses attack settings.
            if (hitData != null && __instance.m_useAttackSettings) CopiedHits.Add(__instance, Copied);
        }

        [HarmonyPatch(typeof(Aoe), "OnHit"), HarmonyPrefix]
        private static void BeginHit(Aoe __instance, out bool __state)
        {
            __state = Repeated;
            Repeated = CopiedHits.TryGetValue(__instance, out _);
        }

        [HarmonyPatch(typeof(Aoe), "OnHit"), HarmonyFinalizer]
        private static void EndHit(bool __state) => Repeated = __state;
    }
}
