using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class SimulationPresets
    {
        private const string Initialized = "ProgressionFixSimulationPresets";
        private static bool preferenceReady;
        private static bool waitingForProjectiles;
        private static readonly List<Projectile> Projectiles = new List<Projectile>();
        private static readonly AccessTools.FieldRef<Projectile, bool> Hit = AccessTools.FieldRefAccess<Projectile, bool>("m_didHit");
        private static readonly AccessTools.FieldRef<ZNet, SimulationDistance> AppliedDistance = AccessTools.FieldRefAccess<ZNet, SimulationDistance>("m_simulationDistance");
        private static readonly AccessTools.FieldRef<GraphicsSettingsManager, GraphicsSettingsState> PlayerSettings =
            AccessTools.FieldRefAccess<GraphicsSettingsManager, GraphicsSettingsState>("m_currentPlayerSettings");
        internal static SimulationDistance Distance(int level) => new SimulationDistance(Mathf.Clamp(level, 2, 4), 2);
        internal static SimulationDistance Normalize(SimulationDistance distance) => Distance(distance.NearSimulationDistance);
        internal static float Radius(SimulationDistance distance) => 50f * Mathf.Clamp(distance.NearSimulationDistance, 2, 4);
        internal static float LocalRadius => Radius(ZNet.instance == null ? Distance(4) : ZNet.instance.GetSyncedSimulationDistance());

        internal static SimulationDistance AfterProjectiles(SimulationDistance selected)
        {
            var network = ZNet.instance;
            if (network == null || network.IsDedicated() || selected.NearSimulationDistance >= AppliedDistance(network).NearSimulationDistance) return selected;
            for (int index = Projectiles.Count - 1; index >= 0; index--)
            {
                var projectile = Projectiles[index];
                if (!projectile) { Projectiles.RemoveAt(index); continue; }
                var view = projectile.GetComponent<ZNetView>();
                if (Hit(projectile) || !view || !view.IsValid() || !view.IsOwner()) continue;
                waitingForProjectiles = true;
                return AppliedDistance(network);
            }
            return selected;
        }

        internal static void InitializePreference()
        {
            // Nothing is left to do once the preference is set and no handshake waits.
            if (preferenceReady && !waitingForProjectiles) return;
            if (waitingForProjectiles && ZNet.instance != null)
            {
                waitingForProjectiles = false;
                ZNet.instance.SimulationDistanceServerHandshake();
            }
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null || ZNet.instance != null && ZNet.instance.IsDedicated()) return;
            if (preferenceReady || GraphicsSettingsManager.Instance == null) return;
            preferenceReady = true;
            if (PlatformPrefs.GetInt(Initialized, 0) != 0) return;
            PlayerSettings(GraphicsSettingsManager.Instance).m_simulationDistance = 4;
            PlatformPrefs.SetInt("SimulationDistance", 4);
            PlatformPrefs.SetInt(Initialized, 1);
            PlatformPrefs.Save();
        }

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class Shot
        {
            private static void Postfix(Projectile __instance)
            {
                Projectiles.RemoveAll(projectile => !projectile);
                if (!Projectiles.Contains(__instance)) Projectiles.Add(__instance);
            }
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        private static class EndSession
        {
            private static void Postfix() { Projectiles.Clear(); waitingForProjectiles = false; }
        }

        [HarmonyPatch(typeof(GraphicsSettingIntExtentions), nameof(GraphicsSettingIntExtentions.GetRange))]
        private static class SliderRange
        {
            private static void Postfix(GraphicsSettingInt obj, ref RangeIntInclusive __result)
            { if (obj == GraphicsSettingInt.SimulationDistance) __result = new RangeIntInclusive(2, 4); }
        }

        [HarmonyPatch(typeof(Valheim.SettingsGui.GraphicsSettings), "GetSimulationDistanceText")]
        private static class Label
        {
            private static bool Prefix(int level, ref string __result)
            {
                __result = Localization.instance.Localize(level <= 2 ? "$settings_low" : level == 3 ? "$settings_medium" : "$settings_high");
                return false;
            }
        }

        [HarmonyPatch(typeof(GraphicsSettingsManager), "LoadGraphicsSettingsFromPlayerPrefs")]
        private static class Load
        {
            private static void Postfix(ref GraphicsSettingsState ___m_currentPlayerSettings) =>
                ___m_currentPlayerSettings.m_simulationDistance = PlatformPrefs.GetInt(Initialized, 0) == 0 ? 4 : Mathf.Clamp(___m_currentPlayerSettings.m_simulationDistance, 2, 4);
        }

        [HarmonyPatch(typeof(GraphicsSettingsManager), nameof(GraphicsSettingsManager.SetGraphicsSettingsFromPreset),
            new[] { typeof(GraphicsModeConfiguration), typeof(GraphicsSettingsState), typeof(GraphicsSettingsPreset), typeof(bool) },
            new[] { ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal })]
        private static class PreserveChoice
        {
            private static void Prefix(ref GraphicsSettingsState state, out int __state) => __state = Mathf.Clamp(state.m_simulationDistance, 2, 4);
            private static void Postfix(ref GraphicsSettingsState state, int __state) => state.m_simulationDistance = __state;
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.ZonesWithinRadius))]
        private static class NearCoverage
        {
            private static void Postfix(Vector2s zoneA, Vector2s zoneB, int zoneRadius, bool ghostZone, ref bool __result)
            {
                if (!ghostZone && (zoneRadius == 2 || zoneRadius == 3))
                    __result = Math.Abs(zoneA.x - zoneB.x) <= zoneRadius && Math.Abs(zoneA.y - zoneB.y) <= zoneRadius;
            }
        }
    }
}
