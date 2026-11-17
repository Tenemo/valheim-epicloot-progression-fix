using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class PersonalBoilingWaters
    {
        private const string TutorialName = "ashlandsocean";
        private const string EncounterKey = Plugin.Id + ".ashlands-ocean";

        // A short crossing counts even if the character retreats before the next global-tutorial poll.
        [HarmonyPostfix, HarmonyPatch(typeof(Player), "FixedUpdate")]
        private static void RecordEncounter(Player __instance)
        {
            if (__instance != Player.m_localPlayer || __instance.IsDead() || __instance.m_customData.ContainsKey(EncounterKey) ||
                __instance.HaveSeenTutorial(TutorialName) || WorldGenerator.GetAshlandsOceanGradient(__instance.transform.position) < 0f) return;
            if (__instance.InWater() || OnFloatingBoat()) __instance.m_customData[EncounterKey] = "1";
        }

        private static bool OnFloatingBoat()
        {
            var ship = Ship.GetLocalShip();
            if (ship == null) return false;
            // Use the native buoyancy cutoff. Trigger membership alone also includes beached or airborne boats.
            var center = ship.GetComponent<Rigidbody>().worldCenterOfMass;
            float water = Floating.GetLiquidLevel(center, 1f, LiquidType.Water);
            return center.y - water - ship.m_waterLevelOffset <= ship.m_disableLevel;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.ShowTutorial))]
        private static bool PersonalWarning(Player __instance, string name)
        {
            if (name != TutorialName || __instance != Player.m_localPlayer) return true;
            RecordEncounter(__instance);
            return __instance.m_customData.ContainsKey(EncounterKey);
        }
    }
}
