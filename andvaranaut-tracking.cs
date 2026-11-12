using System.Collections.Generic;
using EpicLoot.Adventure;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // EpicLoot's Andvaranaut pings only within its range of a treasure chest or bounty target, 20 m by default, and a
    // world without a map cannot show the circles that treasure maps and bounties mark. The add-on does all of the ring's
    // pinging instead, for the wearer's nearest unfound treasure or accepted bounty within the max range, with the ring's
    // far ping in two modes: far, beyond the search radius, while they face the target, and near, within it, on its own.
    // Both quicken as the wearer comes nearer, and only the wearer hears them.
    [HarmonyPatch(typeof(SE_CustomFinder), nameof(SE_CustomFinder.UpdateStatusEffect))]
    internal static class AndvaranautTracking
    {
        private const float ScanInterval = 1f;

        private static readonly AccessTools.FieldRef<List<Beacon>> Beacons =
            AccessTools.StaticFieldRefAccess<List<Beacon>>(AccessTools.Field(typeof(Beacon), "m_instances"));

        private static SE_CustomFinder finder;
        private static bool hasTarget, beyondSearchRadius;
        private static Vector3 targetPosition;
        private static float scanTimer, sincePing;

        // The mode of the local wearer's ring for its status icon, and none while it has no target.
        internal static string ModeLabel(StatusEffect effect) =>
            effect != finder || !hasTarget ? null : beyondSearchRadius ? "Far" : "Near";

        // EpicLoot also runs the wishbone through this class, without required components.
        private static bool IsLocalRing(SE_CustomFinder ring) =>
            Player.m_localPlayer != null && ring.m_character == Player.m_localPlayer
            && ring.RequiredComponentTypes?.Contains(typeof(TreasureMapChest)) == true;

        // A completed bounty must stop being a cached destination immediately, including between one-second scans.
        // Keep the ping cooldown when another active bounty or treasure map becomes the destination.
        internal static void RefreshTarget()
        {
            if (finder == null || !IsLocalRing(finder)) return;
            var player = Player.m_localPlayer;
            FindTarget(player, player.transform.position);
            beyondSearchRadius = hasTarget && Utils.DistanceXZ(player.transform.position, targetPosition) > Plugin.AndvaranautSearchRadius.Value;
            scanTimer = 0f;
        }

        // The add-on does all of the local ring's pinging, so EpicLoot's own pings within its range stay off.
        private static bool Prefix(SE_CustomFinder __instance) => !IsLocalRing(__instance);

        private static void Postfix(SE_CustomFinder __instance, float dt)
        {
            if (!IsLocalRing(__instance)) return;
            var player = Player.m_localPlayer;
            if (__instance != finder)
            {
                finder = __instance;
                hasTarget = false;
                scanTimer = ScanInterval;
                // Equipping starts ready; the distance cooldown begins with the first ping.
                sincePing = Plugin.AndvaranautSlowestPing.Value;
            }
            sincePing += dt;
            var position = player.transform.position;
            scanTimer += dt;
            if (scanTimer >= ScanInterval)
            {
                scanTimer = 0f;
                FindTarget(player, position);
            }
            if (!hasTarget) return;
            float distance = Utils.DistanceXZ(position, targetPosition);
            beyondSearchRadius = distance > Plugin.AndvaranautSearchRadius.Value;
            // Never sooner than the distance's interval after the last ping, so quicker pings always mean a nearer
            // target; looking away only lengthens the gap.
            if (sincePing >= Interval(distance) && (!beyondSearchRadius || IsFacing(player, position))) Ping(__instance, player);
        }

        private static bool IsFacing(Player player, Vector3 position)
        {
            var look = player.GetLookDir();
            var direction = targetPosition - position;
            look.y = 0f;
            direction.y = 0f;
            float facingAngle = Plugin.AndvaranautFacingAngle.Value;
            return facingAngle > 0f && look.sqrMagnitude >= 0.0001f && Vector3.Angle(look, direction) <= facingAngle;
        }

        // The pings quicken in proportion as the target comes nearer: from the ring's slowest rate at the max range to
        // the search radius rate at the search radius, and on to the nearest rate at the target, so the rhythm tells the
        // distance. With no max range, or one within the search radius, the facing pings keep the slowest rate.
        private static float Interval(float distance)
        {
            float searchRadius = Plugin.AndvaranautSearchRadius.Value, maxRange = Plugin.AndvaranautMaxRange.Value;
            float slowest = Plugin.AndvaranautSlowestPing.Value, atSearchRadius = Plugin.AndvaranautSearchRadiusPing.Value;
            if (distance <= searchRadius)
                return Mathf.Lerp(Plugin.AndvaranautNearestPing.Value, atSearchRadius, Mathf.InverseLerp(0f, searchRadius, distance));
            if (maxRange <= searchRadius) return slowest;
            return Mathf.Lerp(atSearchRadius, slowest, Mathf.InverseLerp(searchRadius, maxRange, distance));
        }

        // Created without its network view, the ping plays only on the wearer's game and is never sent to other players.
        private static void Ping(SE_Finder ring, Player player)
        {
            var transform = player.transform;
            ZNetView.m_forceDisableInit = true;
            try
            {
                ring.m_pingEffectFar.Create(transform.position, transform.rotation, transform, 1f, -1, player.GetZDOID());
            }
            finally
            {
                ZNetView.m_forceDisableInit = false;
            }
            sincePing = 0f;
        }

        // The nearest of the player's unfound treasure maps and accepted bounties, if within the max range. The ring aims
        // at the center of the circle EpicLoot marks until the chest or creature itself has loaded.
        private static void FindTarget(Player player, Vector3 position)
        {
            hasTarget = false;
            float nearest = Plugin.AndvaranautMaxRange.Value > 0f ? Plugin.AndvaranautMaxRange.Value : float.MaxValue;
            var adventure = player.GetAdventureSaveData();
            long playerId = player.GetPlayerID();
            foreach (var map in adventure.GetUnfoundTreasureChests())
            {
                if (!LocateChest(map, playerId, out var located)) located = (Vector3)map.Position + (Vector3)map.MinimapCircleOffset;
                Consider(located, position, ref nearest);
            }
            foreach (var bounty in adventure.GetInProgressBounties())
            {
                if (bounty.Slain) continue;
                if (!LocateBounty(bounty.ID, position, out var located)) located = (Vector3)bounty.Position + (Vector3)bounty.MinimapCircleOffset;
                Consider(located, position, ref nearest);
            }
        }

        private static void Consider(Vector3 candidate, Vector3 position, ref float nearest)
        {
            float distance = Utils.DistanceXZ(position, candidate);
            if (distance >= nearest) return;
            nearest = distance;
            hasTarget = true;
            targetPosition = candidate;
        }

        // The player's chest for this map once its area has loaded. It keeps its beacon until it is opened.
        private static bool LocateChest(TreasureMapChestInfo map, long playerId, out Vector3 located)
        {
            foreach (var beacon in Beacons())
            {
                var chest = beacon != null ? beacon.GetComponent<TreasureMapChest>() : null;
                var view = chest != null ? chest.GetComponent<ZNetView>() : null;
                if (view == null || !view.IsValid() || chest.Interval != map.Interval || chest.Biome != map.Biome
                    || view.GetZDO().GetLong("creator") != playerId) continue;
                located = chest.transform.position;
                return true;
            }
            located = default;
            return false;
        }

        // The bounty's loaded main target, while alive. EpicLoot removes a creature's beacon when it takes
        // damage, so it is found by its bounty instead, also during the fight and when it runs off.
        private static bool LocateBounty(string bountyId, Vector3 position, out Vector3 located)
        {
            float nearest = float.MaxValue;
            located = default;
            foreach (var character in Character.GetAllCharacters())
            {
                var view = character != null && !character.IsDead() ? character.GetComponent<ZNetView>() : null;
                if (view == null || !view.IsValid() || !BountyCreature.IsMainTarget(view.GetZDO()) ||
                    view.GetZDO().GetString(BountyCreature.IdKey) != bountyId) continue;
                float distance = Utils.DistanceXZ(position, character.transform.position);
                if (distance >= nearest) continue;
                nearest = distance;
                located = character.transform.position;
            }
            return nearest < float.MaxValue;
        }
    }

    // The ring's status icon shows its mode, where timed effects show their remaining time; the ring has no duration.
    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.GetIconText))]
    internal static class AndvaranautModeLabel
    {
        private static void Postfix(StatusEffect __instance, ref string __result) =>
            __result = AndvaranautTracking.ModeLabel(__instance) ?? __result;
    }
}
