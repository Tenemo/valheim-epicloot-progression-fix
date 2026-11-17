using System;
using EpicLoot.Adventure;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Records each treasure map EpicLoot marks found for the local player, once, when it does so. EpicLoot's save right
    // after removes a found map from an earlier shop interval, so the map is read here, before that save. Taking items,
    // emptying the chest and opening it again record nothing.
    [HarmonyPatch]
    internal static class TreasureDiscoveries
    {
        private static Container opening;

        // EpicLoot marks the map found from its own postfix of this method; the finalizer runs after every postfix.
        [HarmonyPatch(typeof(Container), "RPC_OpenResponse")]
        private static class Opening
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(Container __instance, out Container __state) { __state = opening; opening = __instance; }
            private static Exception Finalizer(Container __state, Exception __exception) { opening = __state; return __exception; }
        }

        [HarmonyPatch(typeof(AdventureSaveData), nameof(AdventureSaveData.FoundTreasureChest))]
        private static class Found
        {
            private static void Postfix(AdventureSaveData __instance, int interval, Heightmap.Biome biome, bool __result)
            {
                if (!__result) return;
                try { Record(__instance.GetTreasureMapChestInfo(interval, biome), interval, biome); }
                catch (Exception error) { ItemActivityChannel.Error(error); }
            }
        }

        // Credit goes to the map's owner only when the map and the chest both name the finding character.
        private static void Record(TreasureMapChestInfo map, int interval, Heightmap.Biome biome)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            long character = player.GetPlayerID();
            var chest = opening;
            var view = chest ? GameFields.ContainerView(chest) : null;
            var data = view && view.IsValid() ? view.GetZDO() : null;
            long creator = data?.GetLong(ZDOVars.s_creator) ?? 0;
            string problem = map == null ? "the map record is missing" : map.PlayerID != character ? $"the map belongs to character {map.PlayerID}" :
                data == null ? "no opened chest was found" : creator != character ? (creator == 0 ? "the chest has no owner" : $"the chest belongs to character {creator}") : null;
            var at = chest ? chest.transform.position : player.transform.position;
            var treasure = new TreasureDiscovery { Owner = problem == null ? character : 0, Biome = biome.ToString(), Interval = interval,
                Chest = data?.m_uid.ToString(), Recorder = Plugin.Version };
            ItemActivityChannel.RecordTreasure(treasure, at, chest ? ItemActivity.Describe(chest) : null, problem == null ? null : "unattributed: " + problem);
        }
    }
}
