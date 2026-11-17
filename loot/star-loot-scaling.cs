using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // StarLevelSystem's PerLevel loot style multiplies a creature's level-scaled drops by level * (PerLevelLootScale +
    // distance bonus), level being stars + 1, rounded to one whole number for the kill. At the pack's 0.5 that number
    // rises every second star: a 1-star creature dropped what a 0-star one does, and 2 and 3 stars dropped the same. Its
    // custom tables could even multiply a drop by less than 1, so a 1-star Oozer dropped less ooze than a 0-star one and
    // no iron scrap. Scale by 1 + stars * (PerLevelLootScale + distance bonus) instead, so every star adds the same
    // share: a level-scaled drop's chance is multiplied by it, and each amount is multiplied by it and rounded up with the
    // probability of its fraction. A custom table entry's AmountScaleFactor sets its share of that per-star increase.
    internal static class StarLootScaling
    {
        private static readonly AccessTools.FieldRef<CharacterDrop, Character> CharacterOf = AccessTools.FieldRefAccess<CharacterDrop, Character>("m_character");
        private static ConfigEntry<float> perLevelScale;
        private static FieldInfo lootStyle;
        private static object perLevelStyle;
        private static MethodInfo distanceBonusOf;
        private static PropertyInfo minimumBonus, maximumBonus;
        // Set for the creature GenerateDropList drops from, before any of its drops reads them.
        private static bool smooth;
        private static float multiplier;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var assembly = StarLevelSystem.Assembly;
            var styles = assembly.GetType("StarLevelSystem.modules.Loot.LootStyles");
            var bonus = assembly.GetType("StarLevelSystem.common.DataObjects+DistanceLootModifier");
            perLevelScale = AccessTools.Field(assembly.GetType("StarLevelSystem.common.ValConfig"), "PerLevelLootScale")?.GetValue(null) as ConfigEntry<float>;
            lootStyle = AccessTools.Field(styles, "SelectedLootFactor");
            perLevelStyle = lootStyle != null && lootStyle.FieldType.IsEnum && Enum.IsDefined(lootStyle.FieldType, "PerLevel")
                ? Enum.Parse(lootStyle.FieldType, "PerLevel") : null;
            distanceBonusOf = bonus == null ? null : AccessTools.Method(styles, "SelectCharacterLootSettings", new[] { typeof(Character), bonus.MakeByRefType() });
            minimumBonus = AccessTools.Property(bonus, "MinAmountScaleFactorBonus");
            maximumBonus = AccessTools.Property(bonus, "MaxAmountScaleFactorBonus");
            var customAmount = bonus == null ? null
                : AccessTools.Method(styles, "MultiplyLootPerLevel", new[] { typeof(int), typeof(int), bonus, typeof(float), typeof(StringBuilder) });
            if (perLevelScale == null || perLevelStyle == null || distanceBonusOf == null || minimumBonus == null || maximumBonus == null || customAmount == null)
                throw new InvalidOperationException("StarLevelSystem's loot scaling changed.");
            var self = typeof(StarLootScaling);
            // Runs after SLS's own transpiler, which puts its multiplier where the game computes 2^(level - 1).
            harmony.Patch(AccessTools.Method(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList)),
                transpiler: new HarmonyMethod(self, nameof(ScaleLevelMultiplier)) { priority = Priority.Last });
            harmony.Patch(customAmount, prefix: new HarmonyMethod(self, nameof(ScaleCustomAmount)));
        }

        // GenerateDropList stores the level multiplier in its first local once and reads it twice: converted to a float to
        // multiply a level-scaled drop's chance, and to multiply its amount.
        private static IEnumerable<CodeInstruction> ScaleLevelMultiplier(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int stores = 0, chances = 0, amounts = 0, others = 0;
            for (int index = 0; index < code.Count; index++)
            {
                if (code[index].opcode == OpCodes.Stloc_1)
                {
                    code.InsertRange(index, new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code[index]),
                        CodeInstruction.Call(typeof(StarLootScaling), nameof(Begin)),
                    });
                    index += 2;
                    stores++;
                }
                else if (code[index].opcode == OpCodes.Ldloc_1)
                {
                    var next = index + 1 < code.Count ? code[index + 1] : null;
                    if (next?.opcode == OpCodes.Mul)
                    {
                        next.opcode = OpCodes.Call;
                        next.operand = AccessTools.Method(typeof(StarLootScaling), nameof(ScaleAmount));
                        amounts++;
                    }
                    // The game converts it with conv.r4; SLS replaces that with its own conversion method.
                    else if (next?.opcode == OpCodes.Conv_R4 || next?.operand is MethodInfo method && method.ReturnType == typeof(float))
                    {
                        code.Insert(index + 2, CodeInstruction.Call(typeof(StarLootScaling), nameof(ScaleChance)));
                        chances++;
                    }
                    else others++;
                }
            }
            if (stores != 1 || chances != 1 || amounts != 1 || others != 0)
                throw new InvalidOperationException("Valheim's drop level multiplier changed.");
            return code;
        }

        private static int Begin(int levelMultiplier, CharacterDrop drop)
        {
            var character = CharacterOf(drop);
            smooth = character != null && lootStyle.GetValue(null).Equals(perLevelStyle);
            if (smooth)
            {
                int stars = Math.Max(0, character.GetLevel() - 1);
                float bonus = 0f;
                if (stars > 0)
                {
                    var arguments = new object[] { character, null };
                    distanceBonusOf.Invoke(null, arguments);
                    bonus = BonusOf(arguments[1]);
                }
                multiplier = Multiplier(stars, 1f, bonus);
            }
            return levelMultiplier;
        }

        private static float ScaleChance(float levelMultiplier) => smooth ? multiplier : levelMultiplier;

        private static int ScaleAmount(int amount, int levelMultiplier) => smooth ? RandomRound(amount * multiplier) : amount * levelMultiplier;

        // SLS's custom tables call MultiplyLootPerLevel(amount, level, distance bonus, AmountScaleFactor or 1, debug text)
        // in its PerLevel and ChancePerLevel styles; the pack uses PerLevel.
        private static bool ScaleCustomAmount(int __0, int __1, object __2, float __3, ref int __result)
        {
            if (!lootStyle.GetValue(null).Equals(perLevelStyle)) return true;
            __result = RandomRound(__0 * Multiplier(Math.Max(0, __1 - 1), __3, BonusOf(__2)));
            return false;
        }

        private static float Multiplier(int stars, float share, float bonus) => 1f + stars * share * (perLevelScale.Value + bonus);

        // SLS rolls the bonus between the distance ring's minimum and maximum, which the pack sets equal.
        private static float BonusOf(object distanceBonus) => distanceBonus == null ? 0f
            : UnityEngine.Random.Range((float)minimumBonus.GetValue(distanceBonus), (float)maximumBonus.GetValue(distanceBonus));

        // Rounds up with the probability of the fraction, so the amount averages exactly the scaled value.
        private static int RandomRound(float amount)
        {
            int whole = Mathf.FloorToInt(amount);
            return UnityEngine.Random.value < amount - whole ? whole + 1 : whole;
        }
    }
}
