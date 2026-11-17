using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Mist particles share the ordinary transparent queue with flames. Draw only the actual flame layers afterwards,
    // retaining their native shader, animation, colors and depth test. Shared materials also serve smoke and creature eyes.
    internal static class FlameVisibility
    {
        private static readonly Dictionary<Material, Material> Materials = new Dictionary<Material, Material>();
        internal const int Queue = 3100;

        private static bool IsFlame(ParticleSystemRenderer renderer, Material material)
        {
            string name = material.name.Replace(" (Instance)", "");
            if (name == "candleflame_flipbook") return true;
            if (name != "flameball_flipbook_gradient" && name != "flames_large_flipbook" && name != "fireball_large" &&
                name != "flame" && name != "flame2" && name != "aoe_flame") return false;
            string layer = renderer.name.ToLowerInvariant();
            return layer.Contains("flame") || layer.Contains("burn") || layer.Contains("torch") || layer.Contains("fire");
        }

        internal static void Apply(GameObject root)
        {
            if (!root || ZNet.instance != null && ZNet.instance.IsDedicated()) return;
            foreach (var renderer in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;
                for (int index = 0; index < materials.Length; index++)
                {
                    var original = materials[index];
                    if (!original || original.renderQueue == Queue || !IsFlame(renderer, original)) continue;
                    if (!Materials.TryGetValue(original, out var flame))
                    {
                        flame = new Material(original) { name = original.name, renderQueue = Queue };
                        Materials.Add(original, flame);
                    }
                    materials[index] = flame;
                    changed = true;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class Prefabs
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ZNetScene __instance)
            {
                foreach (var prefab in __instance.m_prefabs) Apply(prefab);
                foreach (var prefab in __instance.m_nonNetViewPrefabs) Apply(prefab);
            }
        }

        [HarmonyPatch(typeof(SoftReferencePrefabSpawner), "SpawnPrefab")]
        private static class LoadedFlames
        {
            // The spawner instantiates its effect beside itself, then destroys only its placeholder.
            private static void Postfix(SoftReferencePrefabSpawner __instance)
            {
                if (__instance.transform.parent) Apply(__instance.transform.parent.gameObject);
            }
        }

        [HarmonyPatch(typeof(EffectList), nameof(EffectList.Create))]
        private static class Effects
        {
            private static void Postfix(GameObject[] __result)
            {
                if (__result != null) foreach (var effect in __result) Apply(effect);
            }
        }
    }
}
