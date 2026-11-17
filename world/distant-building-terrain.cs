using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Extend the client's full terrain to the negotiated total range, so distant buildings stand on edited ground.
    // Native near zones load first; each update still loads at most one zone. The dedicated server keeps the native
    // near area. With full loading level 4 this follows a 6-sector total range automatically.
    // The range is the one ZDOMan.FindSectorObjects creates far objects in (the ghost-zone radius, 0.8 zones beyond the
    // total). With the plain radius, zones in the gap expired while their far-visible pieces and creatures still stood in
    // them, so they never unloaded and ZoneSystem.UpdateTTL scanned every instance for each of them ten times a second.
    // Every loaded zone in range is kept on every update, also while another zone loads, so a long load expires none.
    [HarmonyPatch(typeof(ZoneSystem), "CreateLocalZones")]
    internal static class DistantBuildingTerrain
    {
        private static readonly AccessTools.FieldRef<ZoneSystem, SimulationDistance> Distance =
            AccessTools.FieldRefAccess<ZoneSystem, SimulationDistance>("m_simulationDistance");
        private static readonly Func<ZoneSystem, Vector2s, bool> PokeLocalZone =
            AccessTools.MethodDelegate<Func<ZoneSystem, Vector2s, bool>>(AccessTools.Method(typeof(ZoneSystem), "PokeLocalZone"));
        private static readonly System.Reflection.FieldInfo Zones = AccessTools.Field(typeof(ZoneSystem), "m_zones");
        private static ZoneSystem bound;
        private static Func<Vector2s, bool> isLoaded;

        // The game returns true when it loaded one of its own zones in this update.
        private static void Postfix(ZoneSystem __instance, Vector3 refPoint, ref bool __result)
        {
            if (ZNet.instance == null || ZNet.instance.IsDedicated()) return;
            if (bound != __instance)
            {
                bound = __instance;
                isLoaded = (Func<Vector2s, bool>)Delegate.CreateDelegate(typeof(Func<Vector2s, bool>), Zones.GetValue(__instance), "ContainsKey");
            }
            var distance = Distance(__instance);
            int radius = distance.TotalSimulationDistance;
            var center = ZoneSystem.GetZone(refPoint);
            for (int y = center.y - radius; y <= center.y + radius; y++)
                for (int x = center.x - radius; x <= center.x + radius; x++)
                {
                    var zone = new Vector2s(x, y);
                    if (!distance.IsClassic && !__instance.ZonesWithinRadius(center, zone, radius, true)) continue;
                    // A loaded zone only has its lifetime reset; a missing one loads when no other zone loaded this update.
                    if ((isLoaded(zone) || !__result) && PokeLocalZone(__instance, zone)) __result = true;
                }
        }
    }

    // Loading full terrain farther out does not extend its shader's cutoff. Beyond the near range the shader hides
    // edited ground, leaving distant buildings and fires over the coarse, unedited terrain instead.
    [HarmonyPatch(typeof(Heightmap), "GetLodHideDistance")]
    internal static class DistantTerrainVisibility
    {
        private static void Postfix(ref float __result)
        {
            if (ZNet.instance == null || ZoneSystem.instance == null || ZNet.instance.IsDedicated()) return;
            int radius = ZNet.instance.GetSyncedSimulationDistance().TotalSimulationDistance;
            float zoneSize = ZoneSystem.instance.m_zoneSize;
            __result = Mathf.Max(1, radius) * Mathf.Sqrt(zoneSize * zoneSize * 2f);
        }
    }

}
