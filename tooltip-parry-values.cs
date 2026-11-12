using System;
using System.Text.RegularExpressions;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
    internal static class TooltipParryValues
    {
        private static readonly Regex ParryLine = new Regex(@"\r?\n\$item_parrybonus:[^\r\n]*", RegexOptions.Compiled);
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ItemDrop.ItemData item, int qualityLevel, ref string __result)
        {
            var player = Player.m_localPlayer;
            if (!player || item == null || item.IsUnidentified() || item.m_shared.m_timedBlockBonus <= 1f || string.IsNullOrEmpty(__result)) return;
            var line = ParryLine.Match(__result);
            if (!line.Success) return;
            var prior = WeaponTooltip.AsIfEquipped(item);
            var context = HandOverride.Active;
            if (context == null) return;
            var previousParry = context.Parry;
            try
            {
                context.Parry = false;
                float force = item.GetDeflectionForce(qualityLevel);
                context.Parry = true;
                float armor = item.GetBlockPower(qualityLevel, player.GetSkillFactor(Skills.SkillType.Blocking)) * item.m_shared.m_timedBlockBonus;
                player.GetSEMan().ModifyTimedBlockBonus(ref armor);
                float parryForce = item.GetDeflectionForce(qualityLevel);
                string color = item.GetMagicItem()?.GetColorString() ?? "orange";
                string text = "\nParry armor: <color=" + color + ">" + armor.ToString("0.#") + "</color>";
                if (!Mathf.Approximately(force, parryForce)) text += "\nParry force: <color=" + color + ">" + parryForce.ToString("0.#") + "</color>";
                __result = __result.Insert(line.Index + line.Length, text);
            }
            finally { context.Parry = previousParry; HandOverride.Active = prior; }
        }
    }
}
