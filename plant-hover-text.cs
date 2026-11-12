using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Plant), nameof(Plant.GetHoverText))]
    internal static class PlantHoverText
    {
        private static ConfigEntryBase betterTimer;
        private static ConfigEntry<bool> plantTimer;
        private static Func<double, string> betterTime;
        private static Func<double, double, string> plantTime;
        private static readonly Func<Plant, double> Elapsed = AccessTools.MethodDelegate<Func<Plant, double>>(AccessTools.Method(typeof(Plant), "TimeSincePlanted"));
        private static readonly Func<Plant, float> Duration = AccessTools.MethodDelegate<Func<Plant, float>>(AccessTools.Method(typeof(Plant), "GetGrowTime"));

        internal static void Initialize()
        {
            if (PluginIds.TryGet(PluginIds.BetterUi, out var better))
            {
                var assembly = better.Instance.GetType().Assembly;
                betterTimer = (ConfigEntryBase)(AccessTools.Field(assembly.GetType("BetterUI.Main", true), "timeLeftHoverTextPlant") ??
                    throw new MissingFieldException("BetterUI's plant timer setting moved.")).GetValue(null);
                betterTime = AccessTools.MethodDelegate<Func<double, string>>(AccessTools.Method(assembly.GetType("BetterUI.Patches.Helpers", true), "TimeString", new[] { typeof(double) }));
            }
            if (PluginIds.TryGet(PluginIds.PlantEverything, out var plants))
            {
                var assembly = plants.Instance.GetType().Assembly;
                var config = (AccessTools.Field(assembly.GetType("Advize_PlantEverything.StaticMembers", true), "config") ??
                    throw new MissingFieldException("PlantEverything's configuration moved.")).GetValue(null);
                plantTimer = (ConfigEntry<bool>)(AccessTools.Field(config.GetType(), "enablePlantTimers") ??
                    throw new MissingFieldException("PlantEverything's plant timer setting moved.")).GetValue(config);
                plantTime = AccessTools.MethodDelegate<Func<double, double, string>>(AccessTools.Method(assembly.GetType("Advize_PlantEverything.HoverTextPatches", true), "FormatTime"));
            }
        }

        [HarmonyAfter(PluginIds.BetterUi, PluginIds.PlantEverything), HarmonyPriority(Priority.Last)]
        private static void Postfix(Plant __instance, ref string __result)
        {
            if (__instance.GetStatus() != Plant.Status.Healthy) return;
            string style = betterTimer?.BoxedValue.ToString() ?? "Disabled";
            bool better = style != "Disabled";
            if (!better && plantTimer?.Value != true) return;
            var view = __instance.GetComponent<ZNetView>();
            if (!view || !view.IsValid()) return;
            double elapsed = Elapsed(__instance), duration = Duration(__instance), remaining = duration - elapsed;
            string timer = remaining <= 0.0 ? "Waiting for growth." : better
                ? style == "PercentageDone" ? Mathf.Clamp01((float)(elapsed / duration)).ToString("P0") : betterTime(remaining)
                : plantTime(elapsed / duration, remaining);
            int newline = __result.IndexOf('\n');
            // Both integrations put their timers after the first line. Retain the localized native/name line.
            __result = (newline < 0 ? __result : __result.Substring(0, newline)) + "\n" + timer;
        }
    }
}
