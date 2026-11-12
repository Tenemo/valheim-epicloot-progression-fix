using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class DungeonInteriors
    {
        internal const string ResetKey = "epicloot_progression_interior_reset";
        internal const float InteriorHeight = 3000f;
        // Morkhalla's Malicious Ice, whose destruction starts the persistent Jotun invasion.
        private static readonly int InvasionTrigger = "BlackIce_Start".GetStableHashCode();
        private static readonly AccessTools.FieldRef<ZoneSystem, Dictionary<Vector2s, ZoneSystem.LocationInstance>> Locations =
            AccessTools.FieldRefAccess<ZoneSystem, Dictionary<Vector2s, ZoneSystem.LocationInstance>>("m_locationInstances");
        private static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> GeneratedZones =
            AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");
        private static readonly Func<ZoneSystem, ZoneSystem.ZoneLocation, int, Vector3, Quaternion, ZoneSystem.SpawnMode, List<GameObject>, bool, GameObject> Spawn =
            AccessTools.MethodDelegate<Func<ZoneSystem, ZoneSystem.ZoneLocation, int, Vector3, Quaternion, ZoneSystem.SpawnMode, List<GameObject>, bool, GameObject>>(
                AccessTools.Method(typeof(ZoneSystem), "SpawnLocation"));
        private static ManualLogSource logger;
        private static MethodInfo findEntry, entryForDistance, entryIsDue, captureConfig, rateFor;
        private static FieldInfo entryEnabled, entryGroup, entrySeconds;
        private static FieldInfo worldReady;
        private static MonoBehaviour runner;
        private static bool running;
        private static float nextSweep;
        private static Func<Vector2s, int, ZDO> findProxy;
        private static Action<Vector2s, List<ZDO>> findObjects;
        private static Action<ZDO> destroy;
        private static Operation operation;
        private static readonly HashSet<Vector2s> occupied = new HashSet<Vector2s>();
        private static readonly HashSet<Vector2s> failed = new HashSet<Vector2s>();
        private static readonly Dictionary<Vector2s, Bounds> interiors = new Dictionary<Vector2s, Bounds>();
        private static readonly Dictionary<Vector2s, ZDOID> interiorProxies = new Dictionary<Vector2s, ZDOID>();
        private static readonly Dictionary<ZDOID, Vector2s> playerDungeons = new Dictionary<ZDOID, Vector2s>();

        private sealed class Operation
        {
            internal Vector2s Zone;
            internal Vector3 Entrance;
            internal readonly List<ZDO> Created = new List<ZDO>();
            internal readonly List<ZNetView> Views = new List<ZNetView>();
        }

        internal static bool Enabled => StarLevelSystem.Installed && Plugin.DungeonInteriorResets.Value;
        internal static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log, MonoBehaviour host)
        {
            logger = log;
            runner = host;
            if (!StarLevelSystem.Installed) return;
            var assembly = StarLevelSystem.Assembly;
            var data = assembly.GetType("StarLevelSystem.Data.LocationResetData", true);
            var entry = data.GetNestedType("ResolvedResetEntry", BindingFlags.Public | BindingFlags.NonPublic);
            var targets = assembly.GetType("StarLevelSystem.modules.LocationReset.ResetTargets", true);
            worldReady = AccessTools.Field(assembly.GetType("StarLevelSystem.modules.LocationReset.LocationResetControl", true), "Ready");
            findEntry = AccessTools.Method(data, "TryGetLocationEntry");
            entryForDistance = AccessTools.Method(entry, "ForDistance");
            entryIsDue = AccessTools.Method(entry, "IsDue");
            entryEnabled = AccessTools.Field(entry, "Enabled");
            entryGroup = AccessTools.Field(entry, "GroupName");
            entrySeconds = AccessTools.Field(entry, "ResetSeconds");
            captureConfig = AccessTools.Method(assembly.GetType("StarLevelSystem.modules.LocationReset.LocationResetConfigSnapshot", true), "Capture");
            rateFor = AccessTools.Method(assembly.GetType("StarLevelSystem.modules.LocationReset.ZoneRates", true), "MultiplierFor");
            findProxy = AccessTools.MethodDelegate<Func<Vector2s, int, ZDO>>(AccessTools.Method(targets, "FindLocationProxy", new[] { typeof(Vector2s), typeof(int) }));
            findObjects = AccessTools.MethodDelegate<Action<Vector2s, List<ZDO>>>(AccessTools.Method(assembly.GetType("StarLevelSystem.modules.LocationReset.ZoneObjects", true), "FindObjects"));
            destroy = AccessTools.MethodDelegate<Action<ZDO>>(AccessTools.Method(targets, "DestroyZdo"));
            if (findEntry == null || entryForDistance == null || entryIsDue == null || entryEnabled == null || entryGroup == null || entrySeconds == null || captureConfig == null || rateFor == null || worldReady == null)
                throw new InvalidOperationException("StarLevelSystem's dungeon reset configuration changed.");
            harmony.Patch(AccessTools.Method(typeof(ZoneSystem), "SpawnLocation"), transpiler: new HarmonyMethod(typeof(DungeonInteriors), nameof(FilterExteriorSpawns)));
            harmony.Patch(AccessTools.Method(typeof(ZoneSystem), "CreateLocationProxy"), prefix: new HarmonyMethod(typeof(DungeonInteriors), nameof(KeepEntrance)));
            harmony.Patch(AccessTools.Method(typeof(ZDOMan), "CreateNewZDO", new[] { typeof(Vector3), typeof(int) }), postfix: new HarmonyMethod(typeof(DungeonInteriors), nameof(RecordCreated)));
            harmony.Patch(AccessTools.Method(typeof(ZNetView), "Awake"), postfix: new HarmonyMethod(typeof(DungeonInteriors), nameof(RecordView)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "Shutdown"), postfix: new HarmonyMethod(typeof(DungeonInteriors), nameof(ClearWorld)));
        }

        internal static void Tick()
        {
            if (!Enabled || worldReady == null || ZNet.instance == null || !ZNet.instance.IsServer() || !(bool)worldReady.GetValue(null) || ZoneSystem.instance == null || ZDOMan.instance == null) return;
            var host = Player.m_localPlayer;
            if (host != null) TrackEntry(host.GetZDOID(), host.transform.position);
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady()) continue;
                var character = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (character != null) TrackEntry(peer.m_characterID, character.GetPosition());
            }
            if (running || Time.realtimeSinceStartup < nextSweep) return;
            nextSweep = Time.realtimeSinceStartup + 1f;
            runner.StartCoroutine(Run());
        }

        private static object ConfiguredEntry(ZoneSystem.LocationInstance instance)
        {
            if (instance.m_location == null) return null;
            var arguments = new object[] { instance.m_location.Hash, null };
            if (!(bool)findEntry.Invoke(null, arguments)) return null;
            var entry = entryForDistance.Invoke(arguments[1], new object[] { Utils.DistanceXZ(Vector3.zero, instance.m_position) });
            return (bool)entryEnabled.GetValue(entry) && (string)entryGroup.GetValue(entry) == "Dungeons" ? entry : null;
        }

        // A connected character's native replicated position proves arrival. A reserved teleport
        // destination or a custom occupancy report does not start the timer.
        private static void TrackEntry(ZDOID player, Vector3 position)
        {
            if (!(position.y > InteriorHeight) || float.IsInfinity(position.y) || float.IsNaN(position.x) || float.IsInfinity(position.x) || float.IsNaN(position.z) || float.IsInfinity(position.z))
            {
                playerDungeons.Remove(player);
                return;
            }
            if (playerDungeons.TryGetValue(player, out var known) && Inside(position, known) && interiorProxies.TryGetValue(known, out var identity))
            {
                var entrance = ZDOMan.instance.GetZDO(identity);
                // The same visit cannot arm a fresh cycle while an exit's native position update is pending.
                if (entrance != null) return;
            }
            playerDungeons.Remove(player);
            var center = ZoneSystem.GetZone(position);
            ZDO closest = null;
            Vector2s dungeon = default;
            float distance = float.MaxValue;
            for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                {
                    var zone = new Vector2s(center.x + x, center.y + z);
                    if (failed.Contains(zone) || !GeneratedZones(ZoneSystem.instance).Contains(zone) || !Locations(ZoneSystem.instance).TryGetValue(zone, out var instance) || ConfiguredEntry(instance) == null) continue;
                    var proxy = findProxy(zone, instance.m_location.Hash);
                    if (proxy == null) continue;
                    try { LocateInterior(zone, proxy, instance.m_location); }
                    catch (Exception error)
                    {
                        failed.Add(zone);
                        logger.LogError($"Dungeon entry {instance.m_location.m_prefabName} at {zone.x},{zone.y} failed; automatic checks paused until the world reloads: {error.GetBaseException()}");
                        continue;
                    }
                    if (!Inside(position, zone)) continue;
                    var offset = proxy.GetPosition() - position;
                    float squared = offset.x * offset.x + offset.z * offset.z;
                    if (squared >= distance) continue;
                    closest = proxy; dungeon = zone; distance = squared;
                }
            if (closest == null) return;
            DungeonResetTimers.Start(closest);
            playerDungeons[player] = dungeon;
        }

        private static IEnumerator Run()
        {
            running = true;
            try
            {
                var sweep = Sweep();
                while (sweep.MoveNext()) yield return sweep.Current;
            }
            finally { running = false; }
        }

        private static IEnumerator Sweep()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZoneSystem.instance == null || ZDOMan.instance == null) yield break;
            var config = captureConfig.Invoke(null, null);
            var snapshot = Locations(ZoneSystem.instance).ToArray();
            float slice = Time.realtimeSinceStartup;
            foreach (var pair in snapshot)
            {
                if (!GeneratedZones(ZoneSystem.instance).Contains(pair.Key) || failed.Contains(pair.Key)) continue;
                try { Reset(pair.Key, config); }
                catch (Exception error)
                {
                    failed.Add(pair.Key);
                    logger.LogError($"Interior reset {pair.Value.m_location?.m_prefabName} at {pair.Key.x},{pair.Key.y} failed; automatic retries paused until the world reloads: {error.GetBaseException()}");
                }
                if (Time.realtimeSinceStartup - slice >= 0.004f)
                {
                    yield return null;
                    slice = Time.realtimeSinceStartup;
                }
            }
        }

        internal static string Reset(Vector2s zone, object config = null)
        {
            if (!Enabled || ZNet.instance == null || !ZNet.instance.IsServer()) return "disabled";
            if (!Locations(ZoneSystem.instance).TryGetValue(zone, out var instance) || instance.m_location == null) return "no location";
            object entry = ConfiguredEntry(instance);
            if (entry == null) return "not a configured dungeon";
            var proxy = findProxy(zone, instance.m_location.Hash);
            if (proxy == null) return "no entrance";
            long previous = DungeonResetTimers.Read(proxy);
            if (previous <= 0) return "not entered";
            config = config ?? captureConfig.Invoke(null, null);
            float rate = (float)rateFor.Invoke(null, new[] { (object)zone, config });
            if (!(bool)entryIsDue.Invoke(entry, new object[] { previous, Now, rate })) return "not due";
            LocateInterior(zone, proxy, instance.m_location);
            if (DungeonOccupancy.ContainsPlayer(zone))
            {
                if (occupied.Add(zone)) logger.LogInfo($"Interior reset {instance.m_location.m_prefabName} at {zone.x},{zone.y} is due; waiting for its occupants to leave.");
                return "occupied";
            }
            occupied.Remove(zone);
            int restored = Rebuild(zone, proxy, instance.m_location);
            proxy.SetOwner(ZDOMan.GetSessionID());
            proxy.Set(ResetKey, Now);
            proxy.Set("SLS_LOC_RESET", proxy.GetLong(ResetKey));
            proxy.Set(DungeonResetTimers.TimerKey, 0L);
            failed.Remove(zone);
            logger.LogInfo($"Interior reset {instance.m_location.m_prefabName} at {zone.x},{zone.y}: restored {restored} objects; exterior preserved.");
            return "reset";
        }

        internal static bool Inside(Vector3 position, Vector2s zone) => position.y > InteriorHeight &&
            (interiors.TryGetValue(zone, out var bounds) ? bounds.Contains(position) : ZoneSystem.GetZone(position).Equals(zone));

        // Vanilla places locations at 22.5-degree yaw steps. Saved entrance angles can lose half a degree
        // (225 becomes 224.5); recover the selected yaw without changing the existing entrance or relaxing the room check.
        private static Quaternion RegenerationRotation(ZDO proxy)
        {
            var angles = proxy.GetRotation().eulerAngles;
            angles.y = Mathf.Round(angles.y / 22.5f) * 22.5f;
            return Quaternion.Euler(angles);
        }

        internal static void LocateInterior(Vector2s zone, ZDO proxy, ZoneSystem.ZoneLocation location)
        {
            if (interiorProxies.TryGetValue(zone, out var known) && known == proxy.m_uid) return;
            location.m_prefab.Load();
            try
            {
                var prefab = location.m_prefab.Asset;
                var component = prefab.GetComponent<Location>();
                var generator = component.m_generator != null ? component.m_generator : prefab.GetComponentInChildren<DungeonGenerator>(true);
                Bounds bounds;
                if (generator != null)
                {
                    var local = prefab.transform.InverseTransformPoint(generator.transform.position);
                    var position = proxy.GetPosition() + RegenerationRotation(proxy) * local;
                    var original = generator.transform.localPosition;
                    bool custom = component.m_useCustomInteriorTransform && component.m_generator != null && component.m_interiorTransform != null;
                    if (custom)
                    {
                        var offset = component.m_interiorTransform.localPosition;
                        position = ZoneSystem.GetZonePos(zone) + offset + original;
                        position.y = proxy.GetPosition().y + offset.y;
                    }
                    var center = ZoneSystem.GetZonePos(ZoneSystem.GetZone(position));
                    center.y = position.y - (custom ? original.y : 0);
                    bounds = new Bounds(center, generator.m_zoneSize);
                    var saved = new List<ZDO>();
                    for (int x = -1; x <= 1; x++)
                        for (int z = -1; z <= 1; z++) findObjects(new Vector2s(zone.x + x, zone.y + z), saved);
                    var record = saved.FirstOrDefault(value => value.GetPrefab() == generator.name.GetStableHashCode() && Vector3.Distance(value.GetPosition(), position) < .1f);
                    if (record == null) throw new InvalidOperationException("The saved dungeon generator could not be located.");
                    AddRoomBounds(ref bounds, record);
                }
                else
                {
                    bounds = new Bounds(proxy.GetPosition() + Vector3.up * 5000, new Vector3(64, 500, 64));
                    foreach (var view in prefab.GetComponentsInChildren<ZNetView>(true))
                    {
                        Vector3 position = proxy.GetPosition() + RegenerationRotation(proxy) * prefab.transform.InverseTransformPoint(view.transform.position);
                        if (position.y > InteriorHeight) bounds.Encapsulate(position);
                    }
                }
                bounds.Expand(4f);
                interiors[zone] = bounds;
                interiorProxies[zone] = proxy.m_uid;
            }
            finally { location.m_prefab.Release(); }
        }

        private static void AddRoomBounds(ref Bounds bounds, ZDO generator)
        {
            if (!generator.GetByteArray("roomData".GetStableHashCode(), out var bytes))
                throw new InvalidOperationException("The dungeon has no saved room layout.");
            using var reader = new BinaryReader(new MemoryStream(bytes));
            int count = reader.ReadInt32();
            for (int index = 0; index < count; index++)
            {
                var room = DungeonDB.instance.GetRoom(reader.ReadInt32());
                Vector3 position = Utils.ReadVector3(reader);
                Quaternion rotation = Utils.ReadQuaternion(reader);
                if (room == null) throw new InvalidOperationException("The dungeon references a missing room.");
                room.m_prefab.Load();
                try
                {
                    var prefab = room.m_prefab.Asset;
                    Vector3 half = (Vector3)prefab.GetComponent<Room>().m_size * .5f;
                    for (int x = -1; x <= 1; x += 2)
                        for (int y = -1; y <= 1; y += 2)
                            for (int z = -1; z <= 1; z += 2) bounds.Encapsulate(position + rotation * Vector3.Scale(half, new Vector3(x, y, z)));
                    foreach (var view in prefab.GetComponentsInChildren<ZNetView>(true))
                        bounds.Encapsulate(position + rotation * prefab.transform.InverseTransformPoint(view.transform.position));
                    foreach (var connection in prefab.GetComponentsInChildren<RoomConnection>(true))
                        bounds.Encapsulate(position + rotation * prefab.transform.InverseTransformPoint(connection.transform.position));
                }
                finally { room.m_prefab.Release(); }
            }
        }

        private static List<ZDO> InteriorObjects(Vector2s zone)
        {
            var found = new List<ZDO>();
            for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++) findObjects(new Vector2s(zone.x + x, zone.y + z), found);
            long owner = OwnerKey(zone);
            return found.Where(value => value != null && value.IsValid() && (Inside(value.GetPosition(), zone) || DungeonFish.BelongsTo(value, zone)) &&
                (value.GetLong("SLS_LOC_OWNER", long.MinValue) == long.MinValue || value.GetLong("SLS_LOC_OWNER") == owner)).Distinct().ToList();
        }

        private static int Rebuild(Vector2s zone, ZDO proxy, ZoneSystem.ZoneLocation location)
        {
            if (operation != null) throw new InvalidOperationException("A dungeon rebuild is already running.");
            var old = InteriorObjects(zone);
            if (old.Count == 0) throw new InvalidOperationException("The dungeon has no saved interior; its entrance was left unchanged.");
            var ambiguousFish = new HashSet<int>(old.Where(value => DungeonFish.Ambiguous(value) && DungeonFish.IsFish(value)).Select(value => value.GetPrefab()));
            var keep = old.Where(value => Preserve(value) || ambiguousFish.Contains(value.GetPrefab()) || DungeonFish.Supplies(value, ambiguousFish)).ToList();
            var current = new Operation { Zone = zone, Entrance = proxy.GetPosition() };
            var ghosts = new List<GameObject>();
            var random = UnityEngine.Random.state;
            location.m_prefab.Load();
            var prefab = location.m_prefab.Asset;
            var originalPosition = prefab.transform.position;
            var originalRotation = prefab.transform.rotation;
            var component = prefab.GetComponent<Location>();
            var interior = component.m_interiorTransform;
            var generator = component.m_generator;
            var interiorPosition = interior != null ? interior.localPosition : Vector3.zero;
            var interiorRotation = interior != null ? interior.localRotation : Quaternion.identity;
            var generatorPosition = generator != null ? generator.transform.localPosition : Vector3.zero;
            bool committed = false;
            try
            {
                operation = current;
                int seed = WorldGenerator.instance.GetSeed() + zone.x * 4271 + zone.y * 9187;
                Spawn(ZoneSystem.instance, location, seed, current.Entrance, RegenerationRotation(proxy), ZoneSystem.SpawnMode.Ghost, ghosts, false);
                if (current.Created.Count == 0) throw new InvalidOperationException("Dungeon generation produced no interior.");
                var outside = current.Created.FirstOrDefault(zdo => !Inside(zdo.GetPosition(), zone));
                if (outside != null)
                    throw new InvalidOperationException($"Dungeon generation crossed its interior boundary: {ZNetScene.instance.GetPrefab(outside.GetPrefab())?.name} at {outside.GetPosition()}, bounds {interiors[zone]}.");
                VerifyRooms(old, current.Created);
                // Generation finishes before anything old is removed. Kept containers retain their exact ZDO and inventory, and
                // Morkhalla's invasion trigger stays exactly as saved: an intact one keeps its damage, a broken one stays gone.
                foreach (var fresh in current.Created.ToArray())
                {
                    bool preserved = DungeonFish.IsFish(fresh)
                        ? ambiguousFish.Contains(fresh.GetPrefab())
                        : DungeonFish.Supplies(fresh, ambiguousFish) || keep.Any(saved => saved.GetPrefab() == fresh.GetPrefab() && (saved.GetPosition() - fresh.GetPosition()).sqrMagnitude < 0.0025f);
                    if (fresh.GetPrefab() != InvasionTrigger && !preserved) continue;
                    destroy(fresh);
                    current.Created.Remove(fresh);
                }
                foreach (var previous in old) if (!keep.Contains(previous)) destroy(previous);
                committed = true;
                return current.Created.Count;
            }
            finally
            {
                operation = null;
                ZNetView.FinishGhostInit();
                foreach (var view in current.Views)
                {
                    if (view == null) continue;
                    view.ResetZDO();
                    UnityEngine.Object.DestroyImmediate(view.gameObject);
                }
                foreach (var ghost in ghosts) if (ghost != null) UnityEngine.Object.DestroyImmediate(ghost);
                if (!committed) foreach (var created in current.Created) if (created.IsValid()) destroy(created);
                prefab.transform.SetPositionAndRotation(originalPosition, originalRotation);
                if (interior != null) { interior.localPosition = interiorPosition; interior.localRotation = interiorRotation; }
                if (generator != null) generator.transform.localPosition = generatorPosition;
                foreach (var spawn in prefab.GetComponentsInChildren<RandomSpawn>(true)) spawn.Reset();
                foreach (var choice in prefab.GetComponentsInChildren<RandomObject>(true)) choice.Reset();
                location.m_prefab.Release();
                UnityEngine.Random.state = random;
            }
        }

        private static bool Preserve(ZDO zdo)
        {
            var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (prefab == null)
                throw new InvalidOperationException($"Interior object {zdo.m_uid} at {zdo.GetPosition()} has prefab hash {zdo.GetPrefab()}, which no installed mod registers; its dungeon was left unchanged.");
            if (prefab.GetComponent<Fish>()) return DungeonFish.Preserve(zdo);
            if (prefab.GetComponent<Player>() != null || prefab.GetComponent<TombStone>() != null || prefab.GetComponent<ItemDrop>() != null ||
                zdo.GetPrefab() == InvasionTrigger) return true;
            // Original dungeon chests regenerate with fresh loot regardless of their current contents.
            return zdo.GetLong("creator") != 0 || zdo.GetBool("tamed");
        }

        private static void VerifyRooms(List<ZDO> old, List<ZDO> fresh)
        {
            int rooms = "roomData".GetStableHashCode();
            foreach (var saved in old)
            {
                if (!saved.GetByteArray(rooms, out var before)) continue;
                var replacement = fresh.FirstOrDefault(zdo => zdo.GetPrefab() == saved.GetPrefab() && (zdo.GetPosition() - saved.GetPosition()).sqrMagnitude < 0.0025f);
                if (replacement == null || !replacement.GetByteArray(rooms, out var after) || !SameRooms(before, after))
                    throw new InvalidOperationException("The regenerated room layout differs from the saved dungeon; the existing interior was retained.");
            }
        }

        internal static bool SameRooms(byte[] before, byte[] after)
        {
            using var first = new BinaryReader(new MemoryStream(before));
            using var second = new BinaryReader(new MemoryStream(after));
            int count = first.ReadInt32();
            if (count != second.ReadInt32()) return false;
            for (int i = 0; i < count; i++)
                if (first.ReadInt32() != second.ReadInt32() || Vector3.Distance(Utils.ReadVector3(first), Utils.ReadVector3(second)) > 0.01f || Quaternion.Angle(Utils.ReadQuaternion(first), Utils.ReadQuaternion(second)) > 0.01f) return false;
            return first.BaseStream.Position == before.Length && second.BaseStream.Position == after.Length;
        }

        private static IEnumerable<CodeInstruction> FilterExteriorSpawns(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(AccessTools.PropertyGetter(typeof(GameObject), nameof(GameObject.activeSelf))),
                AccessTools.Method(typeof(DungeonInteriors), nameof(SpawnInteriorView)), 1,
                "Valheim's location spawn loop changed; expected one network-object filter.");

        private static bool SpawnInteriorView(GameObject prefab) => prefab.activeSelf && (operation == null || prefab.transform.position.y + operation.Entrance.y > InteriorHeight);
        private static bool KeepEntrance() => operation == null;
        private static long OwnerKey(Vector2s zone) => unchecked((long)(((ulong)(uint)(int)zone.x << 32) | (uint)(int)zone.y));
        private static void RecordCreated(ZDO __result)
        {
            if (operation == null || __result == null) return;
            __result.Set("SLS_LOC_OWNER", OwnerKey(operation.Zone));
            operation.Created.Add(__result);
        }
        private static void RecordView(ZNetView __instance) { if (operation != null && __instance.GetZDO() != null) operation.Views.Add(__instance); }
        private static void ClearWorld() { occupied.Clear(); failed.Clear(); interiors.Clear(); interiorProxies.Clear(); playerDungeons.Clear(); running = false; nextSweep = 0; DungeonOccupancy.Clear(); }
    }

    internal static class DungeonResetTimers
    {
        internal const string TimerKey = Plugin.Id + ".dungeon-entry-timer";

        internal static long Read(ZDO proxy) => proxy.GetLong(TimerKey);

        internal static void Start(ZDO proxy)
        {
            if (Read(proxy) > 0) return;
            proxy.SetOwner(ZDOMan.GetSessionID());
            proxy.Set(TimerKey, DungeonInteriors.Now);
        }
    }
}
