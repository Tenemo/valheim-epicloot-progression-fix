using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class AffixDisplayValues
    {
        private static readonly Regex Arguments = new Regex(@"\{(\d+)(?::([^}]+))?\}");
        internal static readonly Regex Numbers = new Regex(@"(?<![\w#.])[+-]?\d+(?:[.,]\d+)?(?:[Ee][+-]?\d+)?(?![\w.])");
        internal static bool IsDropCount(string effectType) => effectType == MagicEffectType.IncreaseMiningDrop || effectType == MagicEffectType.IncreaseTreeDrop;
        // EpicLoot's guard that keeps a skill conversion from counting skill levels converted from another skill.
        private static readonly AccessTools.FieldRef<bool> InConversion =
            AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(typeof(AddSkillLevel_Skills_GetSkillFactor_Patch), "_inSkillsAsSkills"));

        internal static void Initialize()
        {
            foreach (string effectType in new[] { MagicEffectType.IncreaseMiningDrop, MagicEffectType.IncreaseTreeDrop })
                MagicItem.RegisterDisplayValues(effectType, value => new object[] { Math.Max(0, (int)value) });
            // EpicLoot's previews of the skill conversions read Dodge with the levels Blocking converts into it, and added
            // Woodcutting and Pickaxes before rounding down, so 50 Blocking and Dodge previewed +10 each for the +9 that
            // apply, and 15 Woodcutting and Pickaxes +1 Blocking for none. Count them the way the conversions apply.
            MagicItem.RegisterDisplayValues(MagicEffectType.BlockAsDodgeAsBlock, value =>
            {
                var player = Player.m_localPlayer;
                return player == null ? new object[] { value, 0f, 0f }
                    : new object[] { value, (float)Converted(player, Skills.SkillType.Dodge, value), (float)Converted(player, Skills.SkillType.Blocking, value) };
            });
            MagicItem.RegisterDisplayValues(MagicEffectType.BlockAsWoodCuttingAndPickaxes, value =>
            {
                var player = Player.m_localPlayer;
                return new object[] { value, player == null ? 0
                    : Converted(player, Skills.SkillType.WoodCutting, value) + Converted(player, Skills.SkillType.Pickaxes, value) };
            });
        }

        // The levels a conversion takes from one skill: its level without any converted levels, at the value's percentage,
        // rounded down on its own, as EpicLoot's skill bonus applies it.
        private static int Converted(Player player, Skills.SkillType source, float value)
        {
            bool converting = InConversion();
            InConversion() = true;
            try { return (int)(player.GetSkills().GetSkillFactor(source) * value); }
            finally { InConversion() = converting; }
        }
        internal static string Format(object value, string preferredFormat, CultureInfo culture)
        {
            // Mono formats Single with seven significant digits; widening retains its value before display rounding.
            if (value is float number) return ((double)number).ToString("0.##", culture);
            return value is IFormattable formatted ? formatted.ToString(preferredFormat, culture) : value.ToString();
        }
        internal static string Number(string effectType, float value) => IsDropCount(effectType)
            ? Math.Max(0, (int)value).ToString(CultureInfo.CurrentCulture) : Format(value, null, CultureInfo.CurrentCulture);
        internal static string Generic(string effectType, string token) => Numbers.Replace(token,
            match => float.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? Number(effectType, value) : match.Value);
        internal static object[] FormatArguments(string template, object[] arguments)
        {
            var result = (object[])arguments.Clone();
            foreach (Match token in Arguments.Matches(Localization.instance.Localize(template)))
            {
                int index = int.Parse(token.Groups[1].Value, CultureInfo.InvariantCulture);
                if (index < arguments.Length && arguments[index] is float)
                    result[index] = Format(arguments[index], token.Groups[2].Success ? token.Groups[2].Value : null, CultureInfo.CurrentCulture);
            }
            return result;
        }
    }

    [HarmonyPatch(typeof(MagicItem), "FormatDisplayText")]
    internal static class AffixTextFormatting
    {
        private static void Prefix(string displayText, ref object[] args) => args = AffixDisplayValues.FormatArguments(displayText, args);
    }

    // EpicLoot previews a tempered, augmented or etched value by putting the same token, such as "10 → 20", into every
    // argument that changes with the value, so an effect that also shows a share of its value previewed the value twice:
    // Darting Thoughts' maximum eitr, half its regeneration, showed -10 → 20 for -5 → 10. Each argument now shows its own
    // old and new value. A token without numbers, such as the X of a generic description, keeps EpicLoot's behavior.
    [HarmonyPatch(typeof(MagicItem), nameof(MagicItem.GetEffectTextGeneric))]
    internal static class AppliedAffixPreview
    {
        private static readonly Func<string, float, object[]> DisplayArguments =
            AccessTools.MethodDelegate<Func<string, float, object[]>>(AccessTools.Method(typeof(MagicItem), "GetDisplayArgs"));
        private static readonly Func<string, object[], string> FormatText =
            AccessTools.MethodDelegate<Func<string, object[], string>>(AccessTools.Method(typeof(MagicItem), "FormatDisplayText"));

        private static bool Prefix(MagicItemEffectDefinition effectDef, ref string valueToken, ref string __result)
        {
            var numbers = AffixDisplayValues.Numbers.Matches(valueToken).Cast<Match>().ToList();
            var values = new List<float>();
            foreach (var number in numbers)
                if (float.TryParse(number.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) values.Add(value);
            if (values.Count == 0 || values.Count != numbers.Count)
            {
                valueToken = AffixDisplayValues.Generic(effectDef.Type, valueToken);
                return true;
            }
            // As EpicLoot, an argument that is the same for any value is shown as it is.
            object[] atOne = DisplayArguments(effectDef.Type, 1f), atTwo = DisplayArguments(effectDef.Type, 2f);
            var perValue = values.Select(value => DisplayArguments(effectDef.Type, value)).ToList();
            var arguments = new object[atOne.Length];
            string token = valueToken;
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i < atTwo.Length && Equals(atOne[i], atTwo[i]) || perValue.Any(shown => i >= shown.Length))
                {
                    arguments[i] = atOne[i];
                    continue;
                }
                int next = 0;
                arguments[i] = AffixDisplayValues.Numbers.Replace(token,
                    match => AffixDisplayValues.Format(perValue[next++][i], null, CultureInfo.CurrentCulture));
            }
            __result = FormatText(effectDef.DisplayText, arguments);
            return false;
        }
    }

    [HarmonyPatch(typeof(MagicItem), nameof(MagicItem.GetEffectDescription))]
    internal static class AppliedAffixDescription
    {
        private static void Postfix(MagicItemEffectDefinition effectDef, float value, ref string __result)
        {
            __result = __result.Replace("<b><color=yellow>" + value.ToString("0.#", CultureInfo.CurrentCulture) + "</color></b>",
                "<b><color=yellow>" + AffixDisplayValues.Number(effectDef.Type, value) + "</color></b>");
        }
    }
}
