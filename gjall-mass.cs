using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Character), "Awake")]
    internal static class GjallMass
    {
        private static readonly AccessTools.FieldRef<Character, float> OriginalMass =
            AccessTools.FieldRefAccess<Character, float>("m_originalMass");

        [HarmonyPostfix]
        private static void Initialized(Character __instance)
        {
            if (Utils.GetPrefabName(__instance.gameObject) != "Gjall") return;
            // Native SetExtraMass must also use the new base after spawning or loading a saved creature.
            OriginalMass(__instance) = Plugin.GjallBaseMass.Value;
            __instance.GetComponent<Rigidbody>().mass = Plugin.GjallBaseMass.Value;
        }
    }
}
