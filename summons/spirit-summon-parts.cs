using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The parts a creature shows at higher star levels, such as a moose's antlers, a boar's tusks and the arrows in a bear,
    // keep the living animal's material on the Spirit Caller's summons, because the game only ever summons them at one star.
    // Give them the summon's own spirit material on the prefab, so every summon and Star Level System's color copy start from it.
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    internal static class SpiritSummonParts
    {
        private static void Postfix(ZNetScene __instance)
        {
            foreach (var prefab in __instance.m_prefabs.Where(prefab => prefab && prefab.name.EndsWith("_spiritcaller", StringComparison.Ordinal)))
                foreach (var levels in prefab.GetComponentsInChildren<LevelEffects>(true).Where(levels => levels.m_mainRender))
                {
                    var spirit = levels.m_mainRender.sharedMaterial;
                    foreach (var part in levels.m_levelSetups.Where(setup => setup.m_enableObject)
                        .SelectMany(setup => setup.m_enableObject.GetComponentsInChildren<Renderer>(true)))
                        part.sharedMaterials = Enumerable.Repeat(spirit, part.sharedMaterials.Length).ToArray();
                }
        }
    }
}
