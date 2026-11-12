using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Harvestables select their own nearest lights instead of consuming the shared illumination allowance.
    // Decorative particles still pause off screen; picking and regrowth retain their native state.
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    internal static class PickableLights
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ZNetScene __instance)
        {
            foreach (var prefab in __instance.m_prefabs.Where(prefab => prefab && prefab.GetComponent<Pickable>()))
            {
                foreach (var light in prefab.GetComponentsInChildren<Light>(true).Where(light => light.type == LightType.Point))
                    SurfaceLighting.Attach(light);
                foreach (var particles in prefab.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = particles.main;
                    if (main.cullingMode == ParticleSystemCullingMode.AlwaysSimulate) main.cullingMode = ParticleSystemCullingMode.Pause;
                }
            }
        }
    }
}
