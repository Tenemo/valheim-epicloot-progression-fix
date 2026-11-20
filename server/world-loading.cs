using System;
using System.Collections.Generic;
using System.Linq;
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

        // EpicLoot places a bounty or treasure chest on the ground only once every object around its map circle has loaded,
        // with its own search rather than the readiness check above, so a fish or loose pickup left out here held it until the
        // player stood beside the circle. A dungeon interior, far above the ground, appears only once its rooms have loaded,
        // which can take many seconds, so a Burial Chamber in those zones kept a bounty waiting for up to 36 s.
        [HarmonyPatch]
        private static class AdventureReadiness
        {
            private static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(EpicLoot.EpicLoot).Assembly.GetType("EpicLoot.Adventure.AdventureSpawnController", true), "TryFindUninstantiatedObject");

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.FindSectorObjects))),
                    AccessTools.Method(typeof(WorldLoading), nameof(FindSurfaceReadyObjects)), 1, "EpicLoot's adventure area search changed.");
        }

        // EpicLoot also waits for every object in the 3x3 zones around the circle, up to 128 m from its centre, though it searches
        // only within 80% of the circle's radius. Someone travelling fast kept loading new zones beyond the circle, so a bounty
        // still waited seconds after they had passed over it. Only objects within reach of the search hold it now, and wards,
        // whose protected areas EpicLoot keeps a target out of.
        [HarmonyPatch]
        private static class AdventureReach
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var controller = typeof(EpicLoot.EpicLoot).Assembly.GetType("EpicLoot.Adventure.AdventureSpawnController", true);
                yield return AccessTools.Method(controller, "IsAreaSettled");
                yield return AccessTools.Method(controller, "IsCandidateZoneReady");
            }

            private static void Prefix(MonoBehaviour __instance, object[] __args) =>
                adventureCentre = __args[0] is Vector3 point ? point : SearchCentre(__instance);

            private static Exception Finalizer(Exception __exception) { adventureCentre = null; return __exception; }
        }

        // A player's game creates at most 10 objects a pass, 30 passes a second: terrain first, then solid objects, then the
        // rest, each nearest first. Riding fast into a dense place leaves thousands waiting, and a bounty's or treasure's
        // spawner came after all of them, so an Ashlands target appeared 11 s after its rider stopped beside the circle. A
        // spawner, and while its search waits everything within its reach, now comes right after terrain, in the game's own
        // order. Solid objects a little beyond the reach join them, so nothing created early stands on a floor not yet there.
        [HarmonyPatch(typeof(ZNetScene), "CreateObjectsSorted")]
        private static class AdventureFirst
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(List<ZDO>), nameof(List<ZDO>.Sort), new[] { typeof(Comparison<ZDO>) })),
                    AccessTools.Method(typeof(WorldLoading), nameof(SortForCreation)), 1, "Valheim's instance creation order changed.");
        }

        [HarmonyPatch]
        private static class AdventureWaiting
        {
            private static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(EpicLoot.EpicLoot).Assembly.GetType("EpicLoot.Adventure.AdventureSpawnController", true), "IsAreaSettled");

            private static void Postfix(bool __result, Vector3 point)
            {
                if (__result) return;
                WaitingSearches.RemoveAll(search => Utils.DistanceXZ(search.Centre, point) < 1f);
                // EpicLoot asks again every quarter of a second while it waits.
                WaitingSearches.Add((point, Time.time + 1f));
            }
        }

        private static readonly int SpawnerPrefab = "EL_SpawnController".GetStableHashCode();
        private const float FloorMargin = 8f;
        private static readonly List<(Vector3 Centre, float Until)> WaitingSearches = new List<(Vector3, float)>();
        private static readonly List<ZDO> FirstObjects = new List<ZDO>(), LaterObjects = new List<ZDO>();

        private static void SortForCreation(List<ZDO> objects, Comparison<ZDO> comparison)
        {
            objects.Sort(comparison);
            if (ZNet.instance == null || ZNet.instance.IsDedicated()) return;
            float now = Time.time;
            WaitingSearches.RemoveAll(search => search.Until < now);
            float reach = AdventureSearchReach;
            bool First(ZDO data)
            {
                if (data.GetPrefab() == SpawnerPrefab) return true;
                float within = reach + (data.Type == ZDO.ObjectType.Solid ? FloorMargin : 0f);
                foreach (var search in WaitingSearches)
                    if (Utils.DistanceXZ(data.GetPosition(), search.Centre) <= within) return true;
                return false;
            }
            int start = 0;
            while (start < objects.Count && objects[start].Type == ZDO.ObjectType.Terrain) start++;
            try
            {
                for (int index = start; index < objects.Count; index++) (First(objects[index]) ? FirstObjects : LaterObjects).Add(objects[index]);
                if (FirstObjects.Count == 0) return;
                objects.RemoveRange(start, objects.Count - start);
                objects.AddRange(FirstObjects);
                objects.AddRange(LaterObjects);
            }
            finally { FirstObjects.Clear(); LaterObjects.Clear(); }
        }

        private static readonly AccessTools.FieldRef<object, Vector3> SearchCentre = AccessTools.FieldRefAccess<Vector3>(
            typeof(EpicLoot.EpicLoot).Assembly.GetType("EpicLoot.Adventure.AdventureSpawnController", true), "searchCentre");
        private static Vector3? adventureCentre;
        private static HashSet<int> wards;

        // EpicLoot's search radius, then a bounty escort's spread and spawn placement's nearby search, with room for a large body.
        private static float AdventureSearchReach => EpicLoot.Adventure.MinimapController.AreaRadius * .8f + 16f;

        private static void FindSurfaceReadyObjects(ZDOMan manager, Vector2s sector, SimulationDistance distance, List<ZDO> near, List<ZDO> distant)
        {
            FindReadyObjects(manager, sector, distance, near, distant);
            near.RemoveAll(data => data.GetPosition().y > DungeonInteriors.InteriorHeight);
            if (!(adventureCentre is Vector3 centre)) return;
            wards ??= new HashSet<int>(ZNetScene.instance.m_prefabs.Where(prefab => prefab.GetComponent<PrivateArea>()).Select(prefab => prefab.name.GetStableHashCode()));
            float reach = AdventureSearchReach;
            near.RemoveAll(data => Utils.DistanceXZ(data.GetPosition(), centre) > reach && !wards.Contains(data.GetPrefab()));
        }

        private static readonly List<ZDO> PlacementObjects = new List<ZDO>();

        // How far placing a creature looks from its point: the 8 m nearby search, a large body, and the 10 m around a spot kept
        // clear of player builds. The 3x3 zones around the point reach up to 128 m.
        private const float PlacementReach = 24f;

        // ZNetScene.IsAreaReady for placing a creature: a point on the ground doesn't wait for a dungeon interior above it, or for
        // objects beyond the placement's reach, which kept a fast traveller's bounty target hidden for seconds after it spawned.
        internal static bool PlacementReady(Vector3 point)
        {
            if (point.y > DungeonInteriors.InteriorHeight) return ZNetScene.instance.IsAreaReady(point);
            var zone = ZoneSystem.GetZone(point);
            // The ground within reach, which can reach into a neighbouring zone.
            for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                    if (!ZoneSystem.instance.IsZoneLoaded(ZoneSystem.GetZone(point + new Vector3(x, 0f, z) * PlacementReach))) return false;
            try
            {
                FindSurfaceReadyObjects(ZDOMan.instance, zone, new SimulationDistance(1, 0), PlacementObjects, null);
                foreach (var data in PlacementObjects)
                    if (Utils.DistanceXZ(data.GetPosition(), point) <= PlacementReach &&
                        ZNetScene.instance.GetPrefab(data.GetPrefab()) != null && !ZNetScene.instance.HaveInstance(data)) return false;
                return true;
            }
            finally { PlacementObjects.Clear(); }
        }
    }
}
