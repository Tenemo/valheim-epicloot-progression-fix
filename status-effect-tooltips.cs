using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
    internal static class StatusEffectDurationUnits
    {
        private static readonly Regex Duration = new Regex(@"(\$se_ttl:\s*<color=[^>]+>[0-9.,]+)(</color>)", RegexOptions.Compiled);
        internal static string WithSeconds(string text) => Duration.Replace(text, "$1 s$2");

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ref string __result)
        {
            if (!string.IsNullOrEmpty(__result)) __result = WithSeconds(__result);
        }
    }

    // The native tooltip calls SetLevel on shared status-effect assets. Work on a fresh copy instead, so inspecting
    // different qualities and skill levels cannot change the prefab later used by an actual cast or equipped item.
    [HarmonyPatch]
    internal static class StatusEffectTooltips
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { "GetStatusEffectTooltip", "GetEquipStatusEffectTooltip", "GetSetStatusEffectTooltip" })
                yield return AccessTools.Method(typeof(ItemDrop.ItemData), name);
        }

        private static bool Prefix(ItemDrop.ItemData __instance, int quality, float skillLevel, MethodBase __originalMethod, ref string __result)
        {
            var shared = __instance.m_shared;
            StatusEffect source;
            bool heading = __originalMethod.Name == "GetStatusEffectTooltip";
            bool attack = heading && shared.m_attackStatusEffect;
            if (__originalMethod.Name == "GetSetStatusEffectTooltip") source = shared.m_setStatusEffect;
            else if (!heading) source = shared.m_equipStatusEffect;
            else source = shared.m_attackStatusEffect ? shared.m_attackStatusEffect : shared.m_consumeStatusEffect ? shared.m_consumeStatusEffect : shared.m_equipStatusEffect;
            __result = "";
            if (!source) return false;
            var preview = Object.Instantiate(source);
            preview.name = source.name;
            var prior = HandOverride.Active;
            try
            {
                if (WeaponTooltipRange.Item == __instance && Player.m_localPlayer)
                {
                    WeaponTooltip.AsIfEquipped(__instance);
                    skillLevel = Player.m_localPlayer.GetSkillLevel(shared.m_skillType);
                }
                preview.SetLevel(quality, skillLevel);
                if (attack && shared.m_attackStatusEffectChance < 1f)
                    __result = "$item_chancetoapplyse <color=orange>" + shared.m_attackStatusEffectChance * 100f + "%</color>\n";
                if (heading) __result += "<color=orange>" + preview.m_name + "</color>\n";
                string description = preview.GetTooltipString();
                if (preview is SE_Shield)
                {
                    string duration = "$se_shield_ttl <color=orange>" + preview.m_ttl.ToString("0");
                    description = description.Replace(duration + "</color>", duration + " s</color>");
                }
                __result += description;
            }
            finally { HandOverride.Active = prior; Object.Destroy(preview); }
            return false;
        }
    }
}
