using System;
using System.Collections.Generic;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class SharedWeather
    {
        private static readonly Func<EnvMan, string, EnvSetup> GetEnvironment =
            AccessTools.MethodDelegate<Func<EnvMan, string, EnvSetup>>(
                AccessTools.Method(typeof(EnvMan), "GetEnv", new[] { typeof(string) }));
        private static readonly Dictionary<Heightmap.Biome, List<EnvEntry>> Choices = new();
        private static EnvMan owner;

        internal static void Initialize(IntegrationPatches harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(EnvMan), nameof(EnvMan.GetAvailableEnvironments)),
                postfix: new HarmonyMethod(typeof(SharedWeather), nameof(ShareConditions)));
            // The server's weight can arrive after the first choices were built; the next weather roll uses it.
            Plugin.ClearWeatherWeight.SettingChanged += (sender, arguments) => Choices.Clear();
        }

        private static void ShareConditions(EnvMan __instance, BiomeSector __0, List<EnvEntry> __result)
        {
            if (__result == null || (__0.Biome != Heightmap.Biome.Meadows && __0.Biome != Heightmap.Biome.BlackForest &&
                __0.Biome != Heightmap.Biome.Plains && __0.Biome != Heightmap.Biome.Ocean)) return;

            if (!ReferenceEquals(owner, __instance))
            {
                owner = __instance;
                Choices.Clear();
            }
            if (!Choices.TryGetValue(__0.Biome, out var choices))
                Choices[__0.Biome] = choices = BuildChoices(__instance, __0.Biome);

            // EnvMan has already applied local climate additions/removals to its temporary list. Replace those
            // ordinary choices, retaining regional sea overrides. Forced event/interior weather bypasses this method.
            __result.RemoveAll(entry => !entry.m_ashlandsOverride && !entry.m_deepnorthOverride);
            __result.InsertRange(0, choices);
        }

        private static List<EnvEntry> BuildChoices(EnvMan environment, Heightmap.Biome biome)
        {
            var result = new List<EnvEntry>(5);
            // Shared weights give ordinary non-clear weather 5 parts against the configured clear weight, 20 by default.
            // Every biome consumes the same native time-seeded roll.
            foreach (var (name, weight) in new[] { ("Rain", 1f), ("LightRain", 1f), ("Misty", 2f), ("Clear", Plugin.ClearWeatherWeight.Value), ("ThunderStorm", 1f) })
            {
                var preset = GetEnvironment(environment, name) ?? throw new InvalidOperationException("Missing shared weather: " + name);
                if (name == "Clear" && (biome == Heightmap.Biome.BlackForest || biome == Heightmap.Biome.Plains))
                    preset = FairWeather(preset, GetEnvironment(environment, biome == Heightmap.Biome.BlackForest ? "DeepForest Mist" : "Heath clear"));
                result.Add(new EnvEntry { m_environment = preset.m_name, m_env = preset, m_weight = weight });
            }
            return result;
        }

        private static EnvSetup FairWeather(EnvSetup clear, EnvSetup lighting)
        {
            if (lighting == null) throw new InvalidOperationException("Missing biome fair-weather lighting.");
            // Retain the biome palette on a private copy. Fog density, particles, wind and weather effects stay
            // identical to Clear; mutating the vanilla presets would also change forced weather and other biomes.
            var result = clear.Clone();
            result.m_name = lighting.m_name;
            result.m_ambColorDay = lighting.m_ambColorDay;
            result.m_ambColorNight = lighting.m_ambColorNight;
            result.m_sunColorMorning = lighting.m_sunColorMorning;
            result.m_sunColorDay = lighting.m_sunColorDay;
            result.m_sunColorEvening = lighting.m_sunColorEvening;
            result.m_sunColorNight = lighting.m_sunColorNight;
            result.m_lightIntensityDay = lighting.m_lightIntensityDay;
            result.m_lightIntensityNight = lighting.m_lightIntensityNight;
            result.m_sunAngle = lighting.m_sunAngle;
            result.m_fogColorMorning = lighting.m_fogColorMorning;
            result.m_fogColorDay = lighting.m_fogColorDay;
            result.m_fogColorEvening = lighting.m_fogColorEvening;
            result.m_fogColorNight = lighting.m_fogColorNight;
            result.m_fogColorSunMorning = lighting.m_fogColorSunMorning;
            result.m_fogColorSunDay = lighting.m_fogColorSunDay;
            result.m_fogColorSunEvening = lighting.m_fogColorSunEvening;
            result.m_fogColorSunNight = lighting.m_fogColorSunNight;
            result.m_ambientOcclusionColor = lighting.m_ambientOcclusionColor;
            result.m_aoIntensityMorning = lighting.m_aoIntensityMorning;
            result.m_aoIntensityDay = lighting.m_aoIntensityDay;
            result.m_aoIntensityEvening = lighting.m_aoIntensityEvening;
            result.m_aoIntensityNight = lighting.m_aoIntensityNight;
            return result;
        }
    }
}
