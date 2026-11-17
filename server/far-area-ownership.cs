using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The server's ownership loop reads only the sectors within its near distance of each player, but a player's game also
    // keeps the far-visible pieces and creatures of its far area and keeps simulating those it owns. Moving away releases
    // them on the way, between the active area and that distance; a portal or a respawn jumps past it, so what it left in
    // the far area stayed owned, running its AI, pathfinding and upkeep for as long as the player stayed at that distance,
    // unless another player came near it. Nothing in the far area is within the native active area or creature ownership
    // range, so the server now releases what the player owns there.
    [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
    internal static class FarAreaOwnership
    {
        private static readonly List<ZDO> Near = new List<ZDO>();
        private static readonly List<ZDO> Far = new List<ZDO>();

        // Valheim 1.0.15: ReleaseNearbyZDOS(Vector3 refPosition, long uid) runs every two seconds on the server for its own
        // player and for each peer. A mod that replaces that pass calls Release itself, for the same positions.
        private static void Postfix(ZDOMan __instance, Vector3 refPosition, long uid) => Release(__instance, refPosition, uid);

        internal static void Release(ZDOMan manager, Vector3 refPosition, long uid)
        {
            bool local = uid == ZDOMan.GetSessionID();
            if (local && ZNet.instance.IsDedicated()) return;
            var peer = local ? null : ZNet.instance.GetPeer(uid);
            if (!local && peer == null) return;
            var native = ZNet.instance.GetSyncedSimulationDistance();
            int far = (local ? native : peer.m_simulationDistance).TotalSimulationDistance - native.NearSimulationDistance;
            if (far <= 0) return;
            manager.FindSectorObjects(ZoneSystem.GetZone(refPosition), new SimulationDistance(native.NearSimulationDistance, far, native.IsClassic), Near, Far);
            foreach (var data in Far)
                if (data.Persistent && data.GetOwner() == uid) data.SetOwner(0L);
            Near.Clear();
            Far.Clear();
        }
    }
}
