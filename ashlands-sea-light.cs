using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EpicLootProgressionFix
{
    // Past the line where the boiling sea starts to damage ships the water shader blends to its Ashlands colours, but as
    // lit surface colours: from late afternoon until well after dawn the red is too dark to see and nothing marks the line.
    // Those colours are brightened as the red light a level surface receives falls below the level at which the game's own
    // red is clear, which keeps the line visible at every hour and leaves full daylight as the game draws it.
    internal static class AshlandsSeaLight
    {
        // Light is the sun's red reaching a level surface plus the ambient red: 1.67 at clear midday, 0.6 under the moon and
        // 0.36 as the sun crosses the horizon. At ClearLight and above the game's colours stay; below it each 0.1 of light
        // lost adds Gain / 10, which keeps the red beside the game's water about as strong as at clear midday: 3 times at
        // night and 4 at sunrise and sunset. Step keeps the materials from being written for changes nobody can see.
        private const float ClearLight = 1.07f, Gain = 4.5f, Strongest = 5f, Step = 0.05f, Interval = 0.25f, SearchInterval = 10f;
        private static readonly int[] Colours =
        {
            Shader.PropertyToID("_AshlandsColorTop"), Shader.PropertyToID("_AshlandsColorBottom"), Shader.PropertyToID("_AshlandsColorBottomShallow"),
            Shader.PropertyToID("_AshlandsSurfaceColor"), Shader.PropertyToID("_AshlandsFoamColor"),
        };
        private static readonly int SunColour = Shader.PropertyToID("_SunColor"), Distant = Shader.PropertyToID("_IsLod");
        // The game's colours by material name, read before this class first writes a material of that name: a volume's own
        // material and World Performance's shared copy are made from materials that may already be brightened.
        private static readonly Dictionary<string, Color[]> native = new Dictionary<string, Color[]>();
        // What each sea material last received; materials of other water, such as hot tubs, are only remembered.
        private static readonly Dictionary<Material, float> written = new Dictionary<Material, float>();
        private static readonly HashSet<Material> other = new HashSet<Material>();
        private static Material distant;
        private static float nextCheck, nextSearch;
        internal static float Strength { get; private set; } = 1f;
        internal static float Light { get; private set; }

        internal static void Initialize(IntegrationPatches patches)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            patches.Patch(AccessTools.Method(typeof(EnvMan), "SetEnv"), postfix: new HarmonyMethod(typeof(AshlandsSeaLight), nameof(Follow)));
        }

        private static void Follow(EnvMan __instance)
        {
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + Interval;
            Light = Shader.GetGlobalColor(SunColour).r * Mathf.Max(0f, -__instance.GetSunDirection().y) + RenderSettings.ambientLight.r;
            Strength = Mathf.Round(Mathf.Clamp(1f + Gain * (ClearLight - Light), 1f, Strongest) / Step) * Step;
            foreach (var volume in WaterVolume.Instances)
                if (volume.m_waterSurface) Write(volume.m_waterSurface.sharedMaterial);
            // The sea beyond the loaded zones is one plane with its own material, loaded with the world.
            if (!distant && Time.time >= nextSearch)
            {
                nextSearch = Time.time + SearchInterval;
                foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
                    if (material.shader && material.shader.name == "Custom/Water" && material.GetFloat(Distant) > 0.5f) distant = material;
                if (written.Count > 1024) Forget();
            }
            Write(distant);
        }

        private static void Write(Material material)
        {
            if (!material || other.Contains(material)) return;
            if (written.TryGetValue(material, out float last) && last == Strength) return;
            string name = material.name.Replace(" (Instance)", "");
            if (!native.TryGetValue(name, out var colours))
            {
                // Water without Ashlands colours of its own carries the shader's white defaults.
                if (!material.shader || material.shader.name != "Custom/Water" || material.GetColor(Colours[0]) == Color.white)
                {
                    other.Add(material);
                    return;
                }
                native[name] = colours = new Color[Colours.Length];
                for (int index = 0; index < Colours.Length; index++) colours[index] = material.GetColor(Colours[index]);
            }
            for (int index = 0; index < Colours.Length; index++)
            {
                var colour = colours[index];
                material.SetColor(Colours[index], new Color(colour.r * Strength, colour.g * Strength, colour.b * Strength, colour.a));
            }
            written[material] = Strength;
        }

        // Volumes' own materials go with their zones.
        private static void Forget()
        {
            var gone = new List<Material>();
            foreach (var material in written.Keys)
                if (!material) gone.Add(material);
            foreach (var material in gone) written.Remove(material);
            other.RemoveWhere(material => !material);
        }
    }
}
