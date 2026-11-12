using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Beyond the negotiated near area, Valheim sends only far-visible objects through its total range. Include
    // player-built pieces, terrain edits and creatures in that distant ring, alongside native large trees and rocks.
    // Unplaced natural objects and crops retain the native rule. The server and clients run the same selection.
    [HarmonyPatch(typeof(ZDOMan), "FindDistantObjects")]
    internal static class DistantBuildings
    {
        private enum Kind { Other, Piece, Terrain, Creature, Ice }

        private static readonly Dictionary<int, Kind> PrefabKinds = new Dictionary<int, Kind>();
        private static bool creating;

        // Valheim 1.0.15: foreach (ZDO zdo in sectorObjects) if (zdo.Distant) objects.Add(zdo);
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(AccessTools.PropertyGetter(typeof(ZDO), nameof(ZDO.Distant))),
                AccessTools.Method(typeof(DistantBuildings), nameof(IsFarVisible)), 1,
                "Valheim's far object search changed; expected one far-visible check.");

        internal static bool IsFarVisible(ZDO zdo)
        {
            if (zdo.Distant) return true;
            // The dedicated server creates only the objects around its own position, the world's start, where no one sees
            // them, so it keeps the game's rule for those.
            if (creating && ZNet.instance.IsDedicated()) return false;
            int prefab = zdo.GetPrefab();
            if (!PrefabKinds.TryGetValue(prefab, out var kind))
            {
                if (ZNetScene.instance == null) return false;
                var gameObject = ZNetScene.instance.GetPrefab(prefab);
                kind = gameObject == null ? Kind.Other : Classify(gameObject);
                PrefabKinds[prefab] = kind;
            }
            // A terrain edit attaches to its zone's full terrain when it is created, and one created before that terrain exists
            // never applies, even once the player walks in. It is sent at once, and a player's game creates it once the zone
            // has loaded.
            if (kind == Kind.Terrain) return !creating || ZoneSystem.instance.IsZoneLoaded(zdo.GetSector());
            // Creatures this game already runs keep simulating in the far area, and WorldLoading starts a new one there only
            // once this game can control it. Their ice must remain solid there too.
            if (kind == Kind.Creature || kind == Kind.Ice) return true;
            // Server packets can arrive before the far terrain finishes loading. Delay only new client instances;
            // keep already created pieces during asynchronous zone loading and keep sending their records normally.
            return kind == Kind.Piece && zdo.GetLong(ZDOVars.s_creator) != 0L &&
                (!creating || zdo.Created || ZoneSystem.instance.IsZoneLoaded(zdo.GetSector()));
        }

        private static Kind Classify(GameObject prefab) =>
            prefab.GetComponent<TerrainComp>() != null || prefab.GetComponent<TerrainModifier>() != null ? Kind.Terrain
            : prefab.GetComponent<BaseAI>() != null || prefab.GetComponent<Leviathan>() != null ? Kind.Creature
            : IsIceSurface(prefab.name) ? Kind.Ice
            : prefab.GetComponent<Piece>() != null ? Kind.Piece : Kind.Other;

        // Apart from its distant flag, an object can be far-visible only as a piece, terrain edit, creature or ice surface.
        internal static bool CanBeFarVisible(GameObject prefab) => Classify(prefab) != Kind.Other;

        private static bool IsIceSurface(string name) =>
            name.StartsWith("IceShelf_", StringComparison.Ordinal) ||
            name.StartsWith("IceShore", StringComparison.Ordinal) ||
            name.StartsWith("IcePond_", StringComparison.Ordinal) ||
            name.StartsWith("IceWall", StringComparison.Ordinal) ||
            name.StartsWith("IceShard_", StringComparison.Ordinal) ||
            name.StartsWith("ice_rock", StringComparison.Ordinal);

        // The same search chooses what the server sends each player and what each game creates.
        [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
        private static class Creation
        {
            private static void Prefix() => creating = true;
            private static void Finalizer() => creating = false;
        }
    }
}
