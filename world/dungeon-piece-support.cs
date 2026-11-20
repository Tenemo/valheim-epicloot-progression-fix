using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A dungeon's pieces load with its generator, but its rooms only after their models have loaded, a second or more later.
    // The game flags every piece it generates to check its support on its first update instead of after the usual 30 s, so
    // banners, benches and stools found no walls on a dungeon's first load and fell apart into loose materials. A loaded
    // piece in a dungeon interior keeps its wear until the dungeon's last room is placed, and while its zone's generator has
    // not loaded yet, which takes a frame or two, for at most 10 s.
    [HarmonyPatch]
    internal static class DungeonPieceSupport
    {
        private const float GeneratorWaitSeconds = 10f;
        // The game and World Performance's paced placement both keep the saved room list until the last room is placed.
        private static readonly System.Reflection.FieldInfo LoadedRooms = AccessTools.Field(typeof(DungeonGenerator), "m_loadedRooms");
        private static readonly AccessTools.FieldRef<WearNTear, float> CreateTime = AccessTools.FieldRefAccess<WearNTear, float>("m_createTime");
        private static readonly Dictionary<Vector2s, DungeonGenerator> Generators = new Dictionary<Vector2s, DungeonGenerator>();

        [HarmonyPostfix, HarmonyPatch(typeof(DungeonGenerator), "Awake")]
        private static void Loaded(DungeonGenerator __instance) => Generators[ZoneSystem.GetZone(__instance.transform.position)] = __instance;

        [HarmonyPrefix, HarmonyPatch(typeof(DungeonGenerator), "OnDestroy")]
        private static void Unloaded(DungeonGenerator __instance)
        {
            var zone = ZoneSystem.GetZone(__instance.transform.position);
            if (Generators.TryGetValue(zone, out var generator) && generator == __instance) Generators.Remove(zone);
        }

        // Every building piece runs this, so pieces outside interiors return after one height test. A piece placed by a
        // player has no creation time and never waits for a generator.
        [HarmonyPrefix, HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateWear))]
        private static bool Wear(WearNTear __instance, float time)
        {
            Vector3 position = __instance.transform.position;
            if (!Character.InInterior(position)) return true;
            if (!Generators.TryGetValue(ZoneSystem.GetZone(position), out var generator) || !generator)
                return CreateTime(__instance) < 0f || time - CreateTime(__instance) > GeneratorWaitSeconds;
            return !(LoadedRooms.GetValue(generator) is Array rooms && rooms.Length > 0);
        }
    }
}
