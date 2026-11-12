using EpicLoot.Adventure;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Valheim gives each creature either a boss HUD or an overhead HUD. Keep EpicLoot's boss HUD and add a small
    // overhead name and health bar for bounty targets, without changing their boss flag or combat calculations.
    [HarmonyPatch(typeof(BountyTarget), nameof(BountyTarget.Reinitialize))]
    internal static class BountyHealthBars
    {
        private static void Postfix(BountyTarget __instance)
        {
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) return;
            var character = __instance.GetComponent<Character>();
            if (character.m_boss && !__instance.GetComponent<BossOverheadHealthBar>())
                __instance.gameObject.AddComponent<BossOverheadHealthBar>();
        }
    }
}
