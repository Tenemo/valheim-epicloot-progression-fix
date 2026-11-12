using System;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    internal static class ProtectionRadius
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ZNetScene __instance)
        {
            var prefab = __instance.GetPrefab("staff_shield_aoe");
            var area = prefab ? prefab.GetComponent<Aoe>() : null;
            if (!area) throw new InvalidOperationException("Staff of Protection's application effect is missing.");
            area.m_radius = 20f;
        }
    }
}
