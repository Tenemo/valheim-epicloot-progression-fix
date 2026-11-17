using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A triple shot's arrows or bolts land together, but the game gives a creature the sneak-attack bonus at most once
    // every 300 seconds and the first hit alerts it, so only one arrow of the volley got the bonus. The same player's bow
    // and crossbow hits on a creature within a quarter second of its sneak attack now count as part of it: the game's own
    // check lets them through, applies the bonus and plays the sneak-attack effect. The window counts from the first
    // hit, so later hits cannot extend it, and one player's separate shots leave at least 0.8 seconds apart.
    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    internal static class VolleySneakAttack
    {
        private const float Window = 0.25f;
        private static readonly AccessTools.FieldRef<Character, float> SneakAttackTime = AccessTools.FieldRefAccess<Character, float>("m_backstabTime");
        // The latest sneak attack each creature took on this game: who made it and when.
        private static readonly ConditionalWeakTable<Character, SneakAttack> Latest = new ConditionalWeakTable<Character, SneakAttack>();

        private sealed class SneakAttack
        {
            internal ZDOID Attacker;
            internal float Started;
        }

        // Whether a hit belongs to the volley whose sneak attack the creature just took.
        internal static bool Continues(Character creature, HitData hit)
        {
            return (hit.m_skill == Skills.SkillType.Bows || hit.m_skill == Skills.SkillType.Crossbows) && !hit.m_attacker.IsNone() &&
                Latest.TryGetValue(creature, out var latest) && latest.Attacker == hit.m_attacker && Time.time - latest.Started <= Window;
        }

        // Records a sneak attack. The rest of its volley keeps the first hit's time.
        internal static void Record(Character creature, HitData hit)
        {
            if (Continues(creature, hit)) return;
            var latest = Latest.GetOrCreateValue(creature);
            latest.Attacker = hit.m_attacker;
            latest.Started = Time.time;
        }

        private static bool IsAlerted(BaseAI ai, Character creature, HitData hit) => ai.IsAlerted() && !Continues(creature, hit);

        private static float LastSneakAttack(Character creature, HitData hit) =>
            Continues(creature, hit) ? float.NegativeInfinity : SneakAttackTime(creature);

        private static void StartSneakAttack(Character creature, float time, HitData hit)
        {
            Record(creature, hit);
            SneakAttackTime(creature) = time;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var isAlerted = AccessTools.Method(typeof(BaseAI), nameof(BaseAI.IsAlerted));
            var bonus = AccessTools.Field(typeof(HitData), nameof(HitData.m_backstabBonus));
            var time = AccessTools.Field(typeof(Character), "m_backstabTime");
            int alerted = code.FindIndex(x => x.Calls(isAlerted));
            int read = code.FindIndex(x => x.LoadsField(time));
            int write = code.FindIndex(x => x.StoresField(time));
            // Valheim 1.0.15: if (... !m_baseAI.IsAlerted() && hit.m_backstabBonus > 1f && Time.time - m_backstabTime > 300f ...)
            // { m_backstabTime = Time.time; hit.ApplyModifier(hit.m_backstabBonus); ... }
            if (alerted < 0 || code.FindLastIndex(x => x.Calls(isAlerted)) != alerted || code.FindLastIndex(x => x.LoadsField(time)) != read ||
                code.FindLastIndex(x => x.StoresField(time)) != write || !(alerted < read && read < write) || alerted + 3 >= code.Count ||
                !code[alerted + 2].IsLdarg(2) || !code[alerted + 3].LoadsField(bonus) || !code[read - 1].IsLdarg(0) || !code[write - 2].IsLdarg(0))
                throw new InvalidOperationException("Valheim's sneak attack check changed; expected one alert check and one timer.");
            // Each replaced instruction becomes the load of the hit, keeping its labels, followed by the call.
            Replace(code, write, nameof(StartSneakAttack));
            Replace(code, read, nameof(LastSneakAttack));
            Replace(code, alerted, nameof(IsAlerted));
            // The alert check also needs the creature, loaded before the hit.
            code.Insert(alerted, new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code[alerted]));
            return code;
        }

        private static void Replace(List<CodeInstruction> code, int index, string method)
        {
            code[index].opcode = OpCodes.Ldarg_2;
            code[index].operand = null;
            code.Insert(index + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(VolleySneakAttack), method)));
        }
    }
}
