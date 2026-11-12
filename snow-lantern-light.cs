using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(LightLod), "Awake")]
    internal static class SnowLanternLight
    {
        private static void Prefix(LightLod __instance)
        {
            if (__instance.GetComponentInParent<Fireplace>()?.m_name == "$piece_snowlantern")
                __instance.GetComponent<Light>().range = Plugin.SnowLanternLightRadius.Value;
        }
    }
}
