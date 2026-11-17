using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class PlayerGraves
    {
        internal static readonly int Prefab = "Player_tombstone".GetStableHashCode();
        internal static bool IsPlayerGrave(ZDO data) => data.GetPrefab() == Prefab;
    }

    // Keeps every player grave where its owner can reach it. The grave's owning game pauses its physics while the area
    // or dungeon rooms that support it are still loading, after creation, reloading, ownership changes and physical-body
    // replacement, and restores the previous physics once they are ready. A resting grave whose middle is inside solid
    // geometry, or that no player could stand near and see within the native 5 m interaction reach, is moved to the
    // nearest verified spot: its last verified position, its death position, or a nearby spot on the same level. Only
    // what a player's body collides with counts, so tree view blockers, creatures, items and loose ragdolls never block
    // a grave, and a grave partly sunk into a step or rock that can still be opened stays put. The move keeps
    // the same object and inventory, clears its motion and becomes the native tether point, so the native distance check
    // cannot undo it. Floating graves, graves on moving support and graves that already have a verified approach are left
    // alone; when no safe spot exists the grave stays where it is and the reason is logged.
    internal sealed class PlayerGraveSupport : MonoBehaviour
    {
        private static readonly HashSet<DungeonGenerator> Generators = new HashSet<DungeonGenerator>();
        private static readonly HashSet<LocationProxy> Locations = new HashSet<LocationProxy>();
        private static readonly AccessTools.FieldRef<FloatingTerrain, Heightmap> LastGround = AccessTools.FieldRefAccess<FloatingTerrain, Heightmap>("m_lastHeightmap");
        private static readonly AccessTools.FieldRef<FloatingTerrain, float> GroundOffset = AccessTools.FieldRefAccess<FloatingTerrain, float>("m_currentOffset");
        private static readonly AccessTools.FieldRef<FloatingTerrain, float> TargetGroundOffset = AccessTools.FieldRefAccess<FloatingTerrain, float>("m_targetOffset");
        internal static readonly int OriginalDeath = (Plugin.Id + ".grave-death-position").GetStableHashCode();
        internal static readonly int LastSupported = (Plugin.Id + ".grave-supported-position").GetStableHashCode();
        private static readonly Vector3[] Around = Enumerable.Range(0, 8).Select(index => Quaternion.Euler(0f, index * 45f, 0f) * Vector3.forward).ToArray();
        private static readonly Vector3[] Axes = { Vector3.up, Vector3.down, Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
        // The native interaction reaches 5 m from the eye; a little is kept in hand.
        private const float Reach = 4.5f, PlayerRadius = .35f, EyeHeight = 1.6f;
        private static int solid;
        // Only what a player's body collides with blocks a grave or its approach; tree view blockers and water do not.
        internal static int Solid
        {
            get
            {
                if (solid != 0) return solid;
                int character = LayerMask.NameToLayer("character");
                for (int layer = 0; layer < 32; layer++)
                    if (!Physics.GetIgnoreLayerCollision(character, layer) && LayerMask.LayerToName(layer) != "") solid |= 1 << layer;
                return solid;
            }
        }
        private ZNetView view;
        private Floating floating;
        private Rigidbody body;
        private Rigidbody pausedBody;
        private Collider shape;
        private Transform dungeon;
        private Bounds bounds;
        private RigidbodyConstraints constraints;
        private bool gravity, paused, ready, owner;
        private Vector3 velocity, angularVelocity, verified = Vector3.positiveInfinity;
        private string waiting, failure;
        private float nextLookup, nextCheck, nextSearch, restingSince = -1f, verifiedAt;

        private void Awake()
        {
            view = GetComponent<ZNetView>(); floating = GetComponent<Floating>(); RefreshBody();
            if (view && view.IsValid() && view.IsOwner() && body) Pause("its surroundings are still loading");
        }

        private void OnDisable() => Resume();

        private void Pause(string reason)
        {
            if (!paused)
            {
                pausedBody = body;
                constraints = body.constraints; gravity = body.useGravity;
                velocity = body.linearVelocity; angularVelocity = body.angularVelocity;
                if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
                body.constraints = RigidbodyConstraints.FreezeAll; body.useGravity = false;
                paused = true;
            }
            if (waiting == reason) return;
            waiting = reason;
            ZLog.Log("Player grave " + view.GetZDO().m_uid + " waiting: " + reason);
        }

        private void Resume(bool clearMotion = false)
        {
            if (paused && pausedBody)
            {
                pausedBody.constraints = constraints; pausedBody.useGravity = gravity;
                if (!pausedBody.isKinematic)
                {
                    pausedBody.linearVelocity = clearMotion ? Vector3.zero : velocity;
                    pausedBody.angularVelocity = clearMotion ? Vector3.zero : angularVelocity;
                }
            }
            pausedBody = null;
            paused = false; waiting = null;
        }

        // FloatingTerrain can replace the grave's own rigidbody with a separate body; a new body waits for support again.
        private void RefreshBody()
        {
            var active = FloatingTerrain.GetBody(gameObject);
            if (!active) active = GetComponent<Rigidbody>();
            if (active == body && shape && shape.enabled) return;
            bool transfer = paused;
            var previousConstraints = constraints; bool previousGravity = gravity;
            var previousVelocity = velocity; var previousAngular = angularVelocity;
            Resume(); body = active; ready = false;
            shape = body ? body.GetComponentsInChildren<Collider>().FirstOrDefault(collider => collider.enabled && !collider.isTrigger && collider.attachedRigidbody == body) : null;
            // FloatingTerrain copies the old body's settings, including a temporary loading freeze.
            if (transfer && body)
            {
                body.constraints = previousConstraints; body.useGravity = previousGravity;
                if (!body.isKinematic) { body.linearVelocity = previousVelocity; body.angularVelocity = previousAngular; }
            }
        }

        private bool FindDungeon(Vector3 death)
        {
            if (dungeon && bounds.Contains(death)) return true;
            dungeon = null;
            if (Time.realtimeSinceStartup < nextLookup) return false;
            nextLookup = Time.realtimeSinceStartup + .5f;
            foreach (var candidate in Generators)
            {
                if (!candidate || !candidate.gameObject.activeInHierarchy || Mathf.Abs(candidate.transform.position.y - death.y) > 500f ||
                    Utils.DistanceXZ(candidate.transform.position, death) > 256f) continue;
                var rooms = candidate.GetComponentsInChildren<Room>();
                if (rooms.Length == 0) continue;
                var extent = new Bounds(rooms[0].transform.position, Vector3.zero);
                foreach (var room in rooms)
                {
                    var half = (Vector3)room.m_size * .5f;
                    for (int horizontal = -1; horizontal <= 1; horizontal += 2)
                        for (int vertical = -1; vertical <= 1; vertical += 2)
                            for (int depth = -1; depth <= 1; depth += 2)
                                extent.Encapsulate(room.transform.TransformPoint(Vector3.Scale(half, new Vector3(horizontal, vertical, depth))));
                }
                extent.Expand(4f);
                if (!extent.Contains(death)) continue;
                dungeon = candidate.transform; bounds = extent; return true;
            }
            foreach (var location in Locations)
            {
                if (!location || Utils.DistanceXZ(location.transform.position, death) > 256f) continue;
                bool found = false;
                var extent = new Bounds();
                foreach (var collider in location.GetComponentsInChildren<Collider>())
                {
                    if (!collider.enabled || collider.isTrigger || collider.bounds.center.y <= DungeonInteriors.InteriorHeight) continue;
                    if (!found) { extent = collider.bounds; found = true; }
                    else extent.Encapsulate(collider.bounds);
                }
                if (!found) continue;
                extent.Expand(4f);
                if (!extent.Contains(death)) continue;
                dungeon = location.transform; bounds = extent; return true;
            }
            return false;
        }

        // Solid, lasting geometry: not the grave, creatures, items or loose physics bodies such as ragdolls.
        private bool Foreign(Collider collider) =>
            !collider.isTrigger && !collider.transform.IsChildOf(transform) && collider.attachedRigidbody != body &&
            !(collider.attachedRigidbody && !collider.attachedRigidbody.isKinematic) &&
            !collider.GetComponentInParent<Character>() && !collider.GetComponentInParent<ItemDrop>();

        // Within a dungeon only its own geometry counts; elsewhere every static surface does.
        private bool Belongs(Collider collider) => !dungeon || collider.transform.IsChildOf(dungeon);

        // The highest flat surface below a point within the given drop, ignoring the grave and moving things.
        private bool Floor(Vector3 from, float drop, out RaycastHit floor)
        {
            floor = default;
            float closest = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(from, Vector3.down, drop, Solid, QueryTriggerInteraction.Ignore))
            {
                if (hit.normal.y < .6f || hit.distance >= closest || !Foreign(hit.collider) || !Belongs(hit.collider)) continue;
                floor = hit; closest = hit.distance;
            }
            return closest < float.MaxValue;
        }

        // Rays that leave a point through the inside faces of surrounding geometry show the point lies within a solid.
        private bool Enclosed(Vector3 point)
        {
            bool previous = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                int inside = 0;
                foreach (var direction in Axes)
                {
                    RaycastHit nearest = default; float closest = float.MaxValue;
                    foreach (var hit in Physics.RaycastAll(point, direction, 30f, Solid, QueryTriggerInteraction.Ignore))
                        if (hit.distance < closest && Foreign(hit.collider)) { nearest = hit; closest = hit.distance; }
                    if (closest < float.MaxValue && Vector3.Dot(nearest.normal, direction) > 0f) inside++;
                }
                return inside >= 5;
            }
            finally { Physics.queriesHitBackfaces = previous; }
        }

        // The middle of the grave's collider when its body is at this position.
        private Vector3 Center(Vector3 position) => position + (shape.bounds.center - body.position);

        // A grave at this body position does not have its middle inside solid geometry, and an ordinary player can stand
        // on a floor near its level, within about 4 m, with headroom, and see its middle or top within reach. A grave
        // partly sunk into a wall, step or rock that a player can still reach and open is left alone.
        private bool Accessible(Vector3 position)
        {
            var center = Center(position);
            if (Physics.OverlapSphere(center, .1f, Solid, QueryTriggerInteraction.Ignore).Any(Foreign) || Enclosed(center)) return false;
            float bottom = center.y - shape.bounds.extents.y;
            var targets = new[] { center, center + Vector3.up * Mathf.Max(0f, shape.bounds.extents.y - .1f) };
            float nearest = Mathf.Max(shape.bounds.extents.x, shape.bounds.extents.z) + PlayerRadius + .25f;
            for (float spread = nearest; spread < nearest + 3.5f; spread += 1f)
                foreach (var direction in Around)
                {
                    var stand = new Vector3(center.x, bottom, center.z) + direction * spread;
                    if (!Floor(stand + Vector3.up * (EyeHeight + 1f), EyeHeight + 2.5f, out var floor) || floor.point.y > bottom + 1f) continue;
                    var feet = floor.point + Vector3.up * (PlayerRadius + .1f);
                    var head = floor.point + Vector3.up * (EyeHeight + .1f);
                    if (Physics.OverlapCapsule(feet, head, PlayerRadius, Solid, QueryTriggerInteraction.Ignore).Any(Foreign)) continue;
                    var eye = floor.point + Vector3.up * EyeHeight;
                    if (Enclosed(eye)) continue;
                    if (targets.Any(target => Vector3.Distance(eye, target) <= Reach &&
                        !Physics.RaycastAll(eye, target - eye, Vector3.Distance(eye, target), Solid, QueryTriggerInteraction.Ignore).Any(hit => Foreign(hit.collider))))
                        return true;
                }
            return false;
        }

        // The body position that rests the grave on a floor near a point, on the same level as the reference height.
        private bool Resting(Vector3 near, float level, out Vector3 position)
        {
            position = default;
            if (!Floor(new Vector3(near.x, level + 1.5f, near.z), 4f, out var floor) || floor.point.y < level - 2.5f) return false;
            float lift = body.position.y - shape.bounds.min.y;
            position = new Vector3(near.x, floor.point.y + lift + .05f, near.z);
            return true;
        }

        private bool FindDestination(Vector3 death, out Vector3 destination)
        {
            destination = default;
            float level = death.y - .7f;
            var candidates = new List<Vector3>();
            var saved = view.GetZDO().GetVec3(LastSupported, Vector3.positiveInfinity);
            if (!float.IsInfinity(saved.x)) candidates.Add(saved);
            candidates.Add(death);
            foreach (float radius in new[] { 1f, 2f, 3f, 4.5f, 6f })
                foreach (var direction in Around) candidates.Add(death + direction * radius);
            foreach (var candidate in candidates)
            {
                float reference = candidate == saved ? candidate.y - .7f : level;
                if (Resting(candidate, reference, out var position) && Accessible(position)) { destination = position; return true; }
            }
            return false;
        }

        private void Relocate(ZDO data, Vector3 destination, string reason)
        {
            var from = body.position;
            Resume(true);
            body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero;
            var terrain = GetComponent<FloatingTerrain>();
            if (terrain && terrain.m_dummyBody)
            {
                LastGround(terrain) = null; GroundOffset(terrain) = 0f; TargetGroundOffset(terrain) = 0f;
            }
            transform.position = destination; body.position = destination;
            data.SetPosition(destination);
            data.Set(ZDOVars.s_bodyVelHash, Vector3.zero); data.Set(ZDOVars.s_bodyAVelHash, Vector3.zero);
            data.Set(ZDOVars.s_velHash, Vector3.zero);
            if (float.IsInfinity(data.GetVec3(OriginalDeath, Vector3.positiveInfinity).x))
                data.Set(OriginalDeath, data.GetVec3(ZDOVars.s_spawnPoint, from));
            // The native tether returns a grave that strays more than 4 m from this point.
            data.Set(ZDOVars.s_spawnPoint, destination);
            data.Set(LastSupported, destination);
            verified = destination; verifiedAt = Time.time;
            Physics.SyncTransforms();
            ZLog.Log($"Recovered player grave {data.m_uid} from {from} to {destination}: {reason}.");
        }

        private void FixedUpdate()
        {
            RefreshBody();
            if (!view || !view.IsValid() || !body || body.isKinematic) { Resume(); return; }
            if (!view.IsOwner()) { owner = false; Resume(); return; }
            if (!owner) { owner = true; ready = false; }
            var data = view.GetZDO();
            var death = data.GetVec3(OriginalDeath, data.GetVec3(ZDOVars.s_spawnPoint, Vector3.positiveInfinity));
            bool known = !float.IsInfinity(death.y) && !float.IsNaN(death.y);
            if (known && death.y > DungeonInteriors.InteriorHeight)
            {
                if (!ZoneSystem.instance || !ZoneSystem.instance.IsZoneLoaded(death) || !FindDungeon(death)) { Pause("dungeon rooms are not loaded"); return; }
                if (transform.position.y < bounds.min.y)
                {
                    if (!FindDestination(death, out var destination)) { Pause("the death position has no clear supporting floor"); return; }
                    Relocate(data, destination, "it fell below its dungeon");
                }
                if (!Floor(transform.position + Vector3.up, Mathf.Max(2f, transform.position.y + 1f - bounds.min.y), out _)) { Pause("the current dungeon floor is unavailable"); return; }
                ready = true;
            }
            else
            {
                dungeon = null;
                if (!known && transform.position.y > DungeonInteriors.InteriorHeight) { Pause("saved death position is unavailable"); return; }
                if (!ready)
                {
                    if (!ZoneSystem.instance || !ZoneSystem.instance.IsZoneLoaded(transform.position) || !ZNetScene.instance.IsAreaReady(transform.position))
                    { Pause("the surrounding area is still loading"); return; }
                    ready = true;
                }
            }
            Resume();
            if (!known) return;
            Inspect(data, death);
        }

        // A resting grave is checked once a second until verified, and again when it moves or every 30 seconds.
        private void Inspect(ZDO data, Vector3 death)
        {
            if (body.linearVelocity.sqrMagnitude > .04f || body.angularVelocity.sqrMagnitude > .04f) { restingSince = -1f; return; }
            if (restingSince < 0f) restingSince = Time.time;
            if (Time.time - restingSince < 1f || Time.time < nextCheck) return;
            nextCheck = Time.time + 1f;
            if (Vector3.Distance(verified, body.position) < .3f && Time.time - verifiedAt < 30f) return;
            if (floating && floating.BeenFloating() || data.GetBool(ZDOVars.s_inWater)) return;
            if (Physics.Raycast(shape.bounds.center, Vector3.down, out var support, shape.bounds.extents.y + .5f, Solid, QueryTriggerInteraction.Ignore) &&
                (support.collider.attachedRigidbody && !support.collider.attachedRigidbody.isKinematic || support.collider.GetComponentInParent<Ship>())) return;
            if (Accessible(body.position))
            {
                verified = body.position; verifiedAt = Time.time; failure = null;
                var saved = data.GetVec3(LastSupported, Vector3.positiveInfinity);
                if (float.IsInfinity(saved.x) || Vector3.Distance(saved, verified) > .5f) data.Set(LastSupported, verified);
                return;
            }
            nextCheck = Time.time + 5f;
            if (Time.time < nextSearch) return;
            nextSearch = Time.time + 30f;
            if (FindDestination(death, out var destination)) { Relocate(data, destination, "it was enclosed, embedded or out of reach"); return; }
            string reason = "it is enclosed, embedded or out of reach and no verified spot near its death position is free";
            if (failure == reason) return;
            failure = reason;
            ZLog.LogWarning($"Player grave {data.m_uid} at {body.position} needs recovery: {reason}; it and its items stay in place.");
        }

        [HarmonyPatch(typeof(TombStone), "Awake")]
        private static class Attach
        {
            private static void Postfix(TombStone __instance) => __instance.gameObject.AddComponent<PlayerGraveSupport>();
        }

        [HarmonyPatch(typeof(TombStone), "PositionCheck")]
        private static class PositionCheck
        {
            private static bool Prefix(TombStone __instance)
            {
                var support = __instance.GetComponent<PlayerGraveSupport>();
                return support == null || !support.enabled || !support.paused;
            }
        }

        [HarmonyPatch(typeof(DungeonGenerator), "Awake")]
        private static class Loaded
        {
            private static void Postfix(DungeonGenerator __instance) => Generators.Add(__instance);
        }

        [HarmonyPatch(typeof(DungeonGenerator), "OnDestroy")]
        private static class Unloaded
        {
            private static void Prefix(DungeonGenerator __instance) => Generators.Remove(__instance);
        }

        [HarmonyPatch(typeof(LocationProxy), "Awake")]
        private static class LocationLoaded
        {
            private static void Postfix(LocationProxy __instance) => Locations.Add(__instance);
        }

        [HarmonyPatch(typeof(LocationProxy), "OnDestroy")]
        private static class LocationUnloaded
        {
            private static void Prefix(LocationProxy __instance) => Locations.Remove(__instance);
        }
    }
}
