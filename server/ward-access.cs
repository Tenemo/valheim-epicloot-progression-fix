using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The native overlapping-ward rule for one player, without flashing the ward. It also holds on a dedicated server
    // that owns the station and checks a request for that player.
    internal static class WardAccess
    {
        private static readonly Func<PrivateArea, bool> Enabled = AccessTools.MethodDelegate<Func<PrivateArea, bool>>(AccessTools.Method(typeof(PrivateArea), "IsEnabled"));
        private static readonly Func<PrivateArea, Vector3, float, bool> Inside = AccessTools.MethodDelegate<Func<PrivateArea, Vector3, float, bool>>(AccessTools.Method(typeof(PrivateArea), "IsInside"));
        private static readonly Func<PrivateArea, long, bool> Permitted = AccessTools.MethodDelegate<Func<PrivateArea, long, bool>>(AccessTools.Method(typeof(PrivateArea), "IsPermitted"));
        private static readonly List<PrivateArea> Wards = (List<PrivateArea>)AccessTools.Field(typeof(PrivateArea), "m_allAreas").GetValue(null);

        internal static bool Allowed(Component station, long playerId)
        {
            if (playerId == 0L) return false;
            bool guarded = false;
            foreach (var ward in Wards)
            {
                if (!ward || !Enabled(ward) || !Inside(ward, station.transform.position, 0f)) continue;
                guarded = true;
                if (ward.GetComponent<Piece>().GetCreator() == playerId || Permitted(ward, playerId)) return true;
            }
            return !guarded;
        }
    }
}
