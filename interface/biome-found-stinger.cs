using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The fanfare for a newly found biome, which EpicLoot also plays for a completed bounty, has the default sound priority.
    // A Staff of Fracturing barrage keeps more sounds playing than Unity's 32 real voices, so a fanfare that starts during
    // one starts silenced and gets a voice only when one frees, half a second or more into the clip, and its beginning is
    // lost. The highest priority, which the game's ambience loops also have, gives it a voice from its first frame.
    [HarmonyPatch(typeof(MessageHud), "Awake")]
    internal static class BiomeFoundStinger
    {
        private static void Postfix(MessageHud __instance)
        {
            if (!__instance.m_biomeFoundStinger) return;
            foreach (var source in __instance.m_biomeFoundStinger.GetComponentsInChildren<AudioSource>(true)) source.priority = 0;
        }
    }
}
