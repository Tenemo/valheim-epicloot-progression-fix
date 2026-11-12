using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Refine client instances without narrowing server replication, terrain loading or creature ownership.
    // The margin preloads moving creatures and the complete sectors containing their ground and obstacles.
    // The game creates and removes instances up to 30 times a second, so whatever an object's sector already decides is
    // worked out once per sector, each prefab is classified once, and only prefabs that can be far-visible are checked
    // for it.
    internal static class WorldLoading
    {
        internal const float PreloadMargin = 16f;
        internal const float GroundAndObstacleMargin = 32f;
        // An instance that is already loaded stays until it is this much farther out, so a few steps back and forth at the
        // edge do not destroy and re-create whole sectors, and with them a dungeon's interior. Creatures keep their own
        // range. The isolated test client sets 0 to compare the native edge.
        internal static float RemovalMargin = 32f;
        internal const float SwampEffectRadius = 160f;
        internal const float PickupRadius = 160f;
        // A sector counts as entirely within a radius only with this much to spare, so float rounding never decides an
        // object differently from its own distance check.
        private const float Allowance = 1f;
        // The game rounds an object's sector coordinate to a float before flooring it, which can move an object within about
        // a millimeter of an edge into the next sector. Deeper inside, the current sector needs no recalculation.
        private const float SectorInset = .05f;
        private static readonly int SwampMist = "vfx_swamp_mist".GetStableHashCode();
        private static readonly int SwampFlies = "Flies".GetStableHashCode();
        private static readonly int PlayerPrefab = "Player".GetStableHashCode();
        private static readonly int ItemLayer = LayerMask.NameToLayer("item");

        private enum Kind { Other, FarVisible, Creature, Player, Fish, Pickup, Unknown }

        // Unknown prefabs are not kept here, as a mod may still register them.
        private static readonly Dictionary<int, Kind> Kinds = new Dictionary<int, Kind>();
        private static HashSet<string> creatureFoods;
        // A sector's objects come mostly in runs of one prefab, as the world generator places them.
        private static int lastPrefab;
        private static Kind lastKind = Kind.Unknown;

        // The pass in progress: its reference position and what the current sector decides.
        private static Vector3 reference;
        private static float creatureRadius;
        private static long serverPeer;
        private static Vector2s sector;
        private static bool sectorKnown, groundSector, retainedSector, creatureSector, swampSector;
        private static float sectorLowX, sectorHighX, sectorLowZ, sectorHighZ;

        private static float Radius => SimulationPresets.LocalRadius + PreloadMargin;

        internal static bool IntersectsSector(Vector2s sector, Vector3 reference, float radius)
        {
            Vector3 center = ZoneSystem.GetZonePos(sector);
            float half = ZoneSystem.instance.m_zoneSize * .5f;
            float x = Mathf.Max(0f, Mathf.Abs(center.x - reference.x) - half);
            float z = Mathf.Max(0f, Mathf.Abs(center.z - reference.z) - half);
            return x * x + z * z <= radius * radius;
        }

        private static bool InsideSector(Vector2s sector, Vector3 reference, float radius)
        {
            Vector3 center = ZoneSystem.GetZonePos(sector);
            float half = ZoneSystem.instance.m_zoneSize * .5f;
            float x = Mathf.Abs(center.x - reference.x) + half, z = Mathf.Abs(center.z - reference.z) + half;
            return radius > Allowance && x * x + z * z <= (radius - Allowance) * (radius - Allowance);
        }

        private static bool Within(Vector3 position, Vector3 reference, float radius)
        {
            float x = position.x - reference.x, z = position.z - reference.z;
            return x * x + z * z <= radius * radius;
        }

        private static Kind KindOf(int prefab)
        {
            if (prefab == lastPrefab && lastKind != Kind.Unknown) return lastKind;
            if (!Kinds.TryGetValue(prefab, out var kind))
            {
                var definition = ZNetScene.instance.GetPrefab(prefab);
                if (definition == null) return Kind.Unknown;
                if (prefab == PlayerPrefab) kind = Kind.Player;
                else if (definition.GetComponent<Fish>()) kind = Kind.Fish;
                else if (CreaturePrefabs.IsCreature(definition)) kind = Kind.Creature;
                else if (DistantBuildings.CanBeFarVisible(definition)) kind = Kind.FarVisible;
                else
                {
                    var item = definition.GetComponent<ItemDrop>();
                    bool pickup = item || definition.GetComponent<Pickable>() || definition.GetComponent<PickableItem>();
                    kind = pickup && !(item && IsCreatureFood(item)) ? Kind.Pickup : Kind.Other;
                    if (kind == Kind.Pickup)
                        foreach (var collider in definition.GetComponentsInChildren<Collider>(true))
                            if (!collider.isTrigger && collider.gameObject.layer != ItemLayer) { kind = Kind.Other; break; }
                }
                Kinds[prefab] = kind;
            }
            lastPrefab = prefab;
            lastKind = kind;
            return kind;
        }

        private static bool IsCreatureFood(ItemDrop item)
        {
            if (creatureFoods == null)
            {
                creatureFoods = new HashSet<string>();
                foreach (var prefab in ZNetScene.instance.m_prefabs)
                {
                    var animal = prefab.GetComponent<MonsterAI>();
                    if (animal == null || animal.m_consumeItems == null) continue;
                    foreach (var food in animal.m_consumeItems)
                        if (food) creatureFoods.Add(food.m_itemData.m_shared.m_name);
                }
            }
            // The animal's native collider search must still find food throughout its simulation range.
            return creatureFoods.Contains(item.m_itemData.m_shared.m_name);
        }

        private static void Begin(Vector3 position)
        {
            reference = position;
            creatureRadius = Radius;
            serverPeer = ZNet.instance.GetServerPeer()?.m_uid ?? 0L;
            sectorKnown = false;
        }

        // Ground and obstacle sectors retain the simulation supports; one entirely within a radius avoids individual
        // distance checks for its creatures and swamp effects. Fish and loose pickups have their own instance rule.
        private static void Enter(Vector2s next)
        {
            sector = next;
            sectorKnown = true;
            groundSector = IntersectsSector(next, reference, SimulationPresets.LocalRadius + GroundAndObstacleMargin);
            retainedSector = groundSector || IntersectsSector(next, reference, SimulationPresets.LocalRadius + GroundAndObstacleMargin + RemovalMargin);
            creatureSector = InsideSector(next, reference, creatureRadius);
            swampSector = InsideSector(next, reference, SwampEffectRadius);
            Vector3 center = ZoneSystem.GetZonePos(next);
            float inner = ZoneSystem.instance.m_zoneSize * .5f - SectorInset;
            sectorLowX = center.x - inner;
            sectorHighX = center.x + inner;
            sectorLowZ = center.z - inner;
            sectorHighZ = center.z + inner;
        }

        internal static bool Keep(ZDO data, Vector3 reference)
        {
            Begin(reference);
            return Keep(data, false);
        }

        // The distant list holds only objects the far-visible search has just accepted in this pass, which need no second
        // check.
        private static bool Keep(ZDO data, bool far)
        {
            // Native removal destroys locally owned transient ZDOs. Leave projectiles, summoned effects and other
            // transient instances alone.
            if (!data.Persistent) return true;
            // Let ownership settle before unloading a locally controlled object. A record can still name this game as
            // the controller after fast travel, because the server's release never arrived, so start a new creature
            // only when this game could control it now: its area is loaded and it is inside the control range.
            if (data.IsOwner()) return data.Created || !CreaturePrefabs.IsCreature(data) || CreatureOwnership.CanOwn(data, ZDOMan.GetSessionID());
            Vector3 position = data.GetPosition();
            if (!sectorKnown || position.x < sectorLowX || position.x > sectorHighX || position.z < sectorLowZ || position.z > sectorHighZ)
            {
                var next = data.GetSector();
                if (!sectorKnown || next != sector) Enter(next);
            }
            int prefab = data.GetPrefab();
            float margin = data.Created ? RemovalMargin : 0f;
            if (prefab == SwampMist || prefab == SwampFlies)
                return swampSector || Within(position, reference, SwampEffectRadius + margin);
            var kind = KindOf(prefab);
            // Native ownership is assigned from records, independently of instances. Unowned fish are already invisible;
            // distant pickups have no interaction or obstacle role. Owned pickups retain the native path on every client.
            if (!data.HasOwner() && (kind == Kind.Fish || kind == Kind.Pickup && !data.Distant && !Within(position, reference, PickupRadius + margin))) return false;
            if (groundSector && (creatureSector || Within(position, reference, creatureRadius))) return true;
            if (kind == Kind.Creature)
            {
                // Andvaranaut locates a bounty's current position through its loaded main creature.
                if (BountyCreature.IsMainTarget(data)) return true;
                // Another player's active creature can remain visible through the original distant-object range.
                return data.HasOwner() && data.GetOwner() != serverPeer;
            }
            if (groundSector || data.Created && retainedSector || data.Distant || kind == Kind.Player) return true;
            return kind == Kind.Unknown || kind == Kind.FarVisible && (far || DistantBuildings.IsFarVisible(data));
        }

        private static bool Ready(ZDO data) => Keep(data, false);

        private static void Trim(List<ZDO> objects, bool readiness, bool far = false)
        {
            if (objects == null) return;
            int count = objects.Count, kept = 0;
            for (int index = 0; index < count; index++)
            {
                var data = objects[index];
                if (!(readiness ? Ready(data) : Keep(data, far))) continue;
                if (kept != index) objects[kept] = data;
                kept++;
            }
            if (kept < count) objects.RemoveRange(kept, count - kept);
        }

        private static void FindObjects(ZDOMan manager, Vector2s sector, SimulationDistance distance, List<ZDO> near, List<ZDO> distant)
        {
            manager.FindSectorObjects(sector, distance, near, distant);
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            Begin(ZNet.instance.GetReferencePosition());
            Trim(near, false);
            Trim(distant, false, true);
        }

        private static void FindReadyObjects(ZDOMan manager, Vector2s sector, SimulationDistance distance, List<ZDO> near, List<ZDO> distant)
        {
            manager.FindSectorObjects(sector, distance, near, distant);
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            // Intentionally excluded instances must not stall a native teleport or respawn.
            Begin(ZNet.instance.GetReferencePosition());
            Trim(near, true);
        }

        [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
        private static class Instances
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.FindSectorObjects))),
                    AccessTools.Method(typeof(WorldLoading), nameof(FindObjects)), 1, "Valheim's instance sector search changed.");
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady))]
        private static class Readiness
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.FindSectorObjects))),
                    AccessTools.Method(typeof(WorldLoading), nameof(FindReadyObjects)), 1, "Valheim's readiness sector search changed.");
        }

        // EpicLoot places a bounty or treasure chest only once every object around its map circle has loaded, with its own
        // search rather than the readiness check above, so a fish or loose pickup left out here held it until the player
        // stood beside the circle.
        [HarmonyPatch]
        private static class AdventureReadiness
        {
            private static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(EpicLoot.EpicLoot).Assembly.GetType("EpicLoot.Adventure.AdventureSpawnController", true), "TryFindUninstantiatedObject");

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.FindSectorObjects))),
                    AccessTools.Method(typeof(WorldLoading), nameof(FindReadyObjects)), 1, "EpicLoot's adventure area search changed.");
        }
    }
}
