using System.Text.RegularExpressions;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The game's tooltip leaves out chop and pickaxe damage, the values that decide how fast a tool fells trees and breaks
    // rocks. Add them after the other damage rows, with the roll of the skill those hits use: Attack.DoMeleeAttack rolls a
    // tree or rock hit with the attack's special hit skill, such as Woodcutting for an axe, when it has one.
    [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
    internal static class ToolDamageTooltip
    {
        private static readonly Regex DamageLines = new Regex(@"\r?\n\$inventory_(damage|blunt|slash|pierce|fire|frost|lightning|poison|spirit):[^\r\n]*", RegexOptions.Compiled);
        private static readonly Regex Colors = new Regex(@"<color=([^>]+)>", RegexOptions.Compiled);

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ItemDrop.ItemData item, int qualityLevel, float worldLevel, ref string __result)
        {
            var player = Player.m_localPlayer;
            if (!player || item == null || item.IsUnidentified() || string.IsNullOrEmpty(__result)) return;
            if (item.m_shared.m_damages.m_chop + item.m_shared.m_damagesPerLevel.m_chop <= 0f &&
                item.m_shared.m_damages.m_pickaxe + item.m_shared.m_damagesPerLevel.m_pickaxe <= 0f) return;
            Match last = null;
            for (var line = DamageLines.Match(__result); line.Success; line = line.NextMatch()) last = line;
            if (last == null) return;
            var prior = WeaponTooltip.AsIfEquipped(item);
            try
            {
                var damage = item.GetDamage(qualityLevel, worldLevel);
                var special = item.m_shared.m_attack.m_specialHitSkill;
                player.GetSkills().GetRandomSkillRange(out float low, out float high, special != Skills.SkillType.None ? special : item.m_shared.m_skillType);
                // Take the colors of the damage row above, which the game, EpicLoot or the interface mod may have set.
                var colors = Colors.Matches(last.Value);
                string color = colors.Count > 0 ? colors[0].Groups[1].Value : "orange", rangeColor = colors.Count > 1 ? colors[1].Groups[1].Value : "yellow";
                string Row(string label, float value) => value <= 0f ? "" : $"\n{label}: <color={color}>{Mathf.RoundToInt(value)}</color> " +
                    $"<color={rangeColor}>({Mathf.RoundToInt(value * low)}-{Mathf.RoundToInt(value * high)}) </color>";
                __result = __result.Insert(last.Index + last.Length, Row("Chop damage", damage.m_chop) + Row("Mining damage", damage.m_pickaxe));
            }
            finally { HandOverride.Active = prior; }
        }
    }
}
