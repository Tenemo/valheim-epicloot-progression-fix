using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class CraftingCartHealth
    {
        private const string CraftingFactor = "ProgressionFix Cart Crafting";

        internal static void OnPlaced(WearNTear piece)
        {
            if (!piece.GetComponent<Vagon>()) return;
            var data = piece.GetComponent<ZNetView>().GetZDO();
            if (data == null || data.GetFloat(CraftingFactor, out _)) return;
            float factor = BuilderFactor(Player.m_localPlayer);
            data.Set(CraftingFactor, factor);
            piece.m_health *= HealthFactor(factor);
            data.Set(ZDOVars.s_health, piece.m_health);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(WearNTear), "Awake")]
        private static void RestoreHealth(WearNTear __instance)
        {
            if (__instance.GetComponent<Vagon>() &&
                __instance.GetComponent<ZNetView>().GetZDO() is ZDO data && data.GetFloat(CraftingFactor, out float factor))
                __instance.m_health *= HealthFactor(Mathf.Clamp01(factor));
        }

        // The cart saves its builder's skill factor, not the bonus, so the configured bonus applies whenever it loads.
        internal static float HealthFactor(float skill) => 1f + Plugin.CartHealthBonus.Value * skill;

        internal static float BuilderFactor(Player player)
        {
            // Native GetSkillFactor floors the level; cart health retains fractional skill progress.
            float level = player.GetSkills().GetSkillList().FirstOrDefault(skill => skill.m_info.m_skill == Skills.SkillType.Crafting)?.m_level ?? 0f;
            player.GetSEMan().ModifySkillLevel(Skills.SkillType.Crafting, ref level);
            return Mathf.Clamp01(level / 100f);
        }
    }
}
