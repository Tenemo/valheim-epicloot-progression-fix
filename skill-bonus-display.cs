using System.Collections.Generic;
using System.Globalization;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(DisplayExtraSkillLevels_SkillsDialog_Setup_Patch), "Postfix")]
    internal static class SkillBonusDisplay
    {
        private static readonly AccessTools.FieldRef<SkillsDialog, List<GameObject>> Elements =
            AccessTools.FieldRefAccess<SkillsDialog, List<GameObject>>("m_elements");

        // Replace EpicLoot's old extra-bar renderer with the game's existing bonus label and bar.
        // Positional arguments refer to the patched static postfix's dialog and player parameters.
        private static bool Prefix(SkillsDialog __0, Player __1)
        {
            if (EpicLoot.EpicLoot.HasAuga) return true;

            var skills = __1.GetSkills();
            var skillList = skills.GetSkillList();
            var elements = Elements(__0);
            for (int index = 0; index < skillList.Count; index++)
            {
                var skill = skillList[index];
                var element = elements[index].transform;
                int effectiveLevel = Mathf.RoundToInt(EffectiveFactor(skills, skill.m_info.m_skill) * 100f);
                int bonus = effectiveLevel - Mathf.FloorToInt(skill.m_level);
                var bonusText = Utils.FindChild(element, "bonustext").GetComponent<TMP_Text>();
                bonusText.gameObject.SetActive(bonus != 0);
                bonusText.text = bonus.ToString("+0;-0;0", CultureInfo.InvariantCulture);
                Utils.FindChild(element, "levelbar_total").GetComponent<GuiBar>().SetValue(effectiveLevel / 100f);
            }

            return false;
        }

        // Weapon and tool skills show the level their damage roll uses, which keeps the game's bonuses above 100;
        // other skills show the game's factor, which their effects use.
        private static float EffectiveFactor(Skills skills, Skills.SkillType skill) =>
            SkillDamageRange.DamageSkills.Contains(skill) ? SkillDamageRange.DamageFactor(skills, skill) : skills.GetSkillFactor(skill);
    }
}
