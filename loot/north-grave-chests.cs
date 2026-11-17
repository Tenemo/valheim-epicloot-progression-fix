using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The northern graves bury ordinary village chests at -2 m with support wear enabled. A chest wholly below
    // the terrain surface has no overlapping support collider and collapses. Meadows' buried chest disables it.
    internal static class NorthGraveChests
    {
        internal const string RecoveryKey = "ProgressionFix Northern Grave Chests";
        internal const string RecoveryRequestRpc = "ProgressionFix.NorthernGraveRecovery";
        internal static readonly int ChestPrefab = "TreasureChest_deepnorth_village".GetStableHashCode();
        private static readonly int ProxyPrefab = "LocationProxy".GetStableHashCode();
        private static readonly int[] GravePrefabs = { "ShipSetting02".GetStableHashCode(), "ShipSetting03".GetStableHashCode() };
        private static readonly Func<ZoneSystem, int, ZoneSystem.ZoneLocation> GetLocation =
            AccessTools.MethodDelegate<Func<ZoneSystem, int, ZoneSystem.ZoneLocation>>(AccessTools.Method(typeof(ZoneSystem), "GetLocation", new[] { typeof(int) }));
        private static readonly Func<ZoneSystem, Vector2s, bool> LoadZone =
            AccessTools.MethodDelegate<Func<ZoneSystem, Vector2s, bool>>(AccessTools.Method(typeof(ZoneSystem), "PokeLocalZone", new[] { typeof(Vector2s) }));
        private static ManualLogSource logger;
        private static MonoBehaviour runner;
        private static readonly HashSet<ZDOID> Failed = new HashSet<ZDOID>();
        private static readonly HashSet<ZDOID> Pending = new HashSet<ZDOID>();

        internal static void Initialize(ManualLogSource log, MonoBehaviour host) { logger = log; runner = host; }
        internal static bool IsGrave(ZDO proxy) => proxy.GetPrefab() == ProxyPrefab && GravePrefabs.Contains(proxy.GetInt(ZDOVars.s_location));
        internal static float FlatDistance(Vector3 first, Vector3 second) => new Vector2(first.x - second.x, first.z - second.z).magnitude;

        internal static List<ZDO> Nearby(Vector3 position)
        {
            var objects = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(position), new SimulationDistance(1, 0, true), objects);
            return objects;
        }

        internal static bool IsBuriedGraveChest(ZDO chest) => chest.GetPrefab() == ChestPrefab
            && chest.GetLong(ZDOVars.s_creator) == 0
            && Nearby(chest.GetPosition()).Any(proxy => IsGrave(proxy) && FlatDistance(proxy.GetPosition(), chest.GetPosition()) <= 24f);

        internal sealed class ChestSlot
        {
            internal Vector3 Position;
            internal Quaternion Rotation;
        }

        // Use the original location seed and the native random-object ordering, preserving the game's 50% chest
        // choices. Replaying the layout never creates a network object or rolls an inventory.
        internal static List<ChestSlot> OriginalSlots(ZDO proxy)
        {
            var location = GetLocation(ZoneSystem.instance, proxy.GetInt(ZDOVars.s_location));
            if (location == null) throw new InvalidOperationException("Northern grave definition is missing.");
            location.m_prefab.Load();
            var prefab = location.m_prefab.Asset;
            var position = prefab.transform.position;
            var rotation = prefab.transform.rotation;
            var random = UnityEngine.Random.state;
            var views = Utils.GetEnabledComponentsInChildren<ZNetView>(prefab);
            var spawns = Utils.GetEnabledComponentsInChildren<RandomSpawn>(prefab);
            var choices = Utils.GetEnabledComponentsInChildren<RandomObject>(prefab);
            // Native location yaw is chosen in 22.5-degree steps; saved rotations can lose half a degree.
            var angles = proxy.GetRotation().eulerAngles;
            angles.y = Mathf.Round(angles.y / 22.5f) * 22.5f;
            var yaw = Quaternion.Euler(angles);
            try
            {
                prefab.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                foreach (var spawn in spawns) spawn.Prepare();
                UnityEngine.Random.InitState(proxy.GetInt(ZDOVars.s_seed));
                var component = prefab.GetComponent<Location>();
                foreach (var spawn in spawns) spawn.Randomize(proxy.GetPosition() + yaw * spawn.transform.position, component);
                foreach (var choice in choices) choice.Randomize(proxy.GetPosition() + yaw * choice.transform.position, component);
                return views.Where(view => view.gameObject.activeSelf && Utils.GetPrefabName(view.gameObject) == "TreasureChest_deepnorth_village")
                    .Select(view => new ChestSlot { Position = proxy.GetPosition() + yaw * view.transform.position,
                        Rotation = yaw * view.transform.rotation }).ToList();
            }
            finally
            {
                foreach (var spawn in spawns) spawn.Reset();
                foreach (var choice in choices) choice.Reset();
                foreach (var view in views) view.gameObject.SetActive(true);
                prefab.transform.SetPositionAndRotation(position, rotation);
                UnityEngine.Random.state = random;
                location.m_prefab.Release();
            }
        }

        // Terrain compiler records contain player edits even when the dedicated server has no local terrain objects.
        // Read the native saved grid rather than comparing with procedural heights, which omit location flattening.
        internal static bool TerrainWasEdited(Vector3 position, List<ZDO> nearby)
        {
            foreach (var data in nearby)
            {
                var raw = data.GetByteArray(ZDOVars.s_TCData);
                if (raw != null)
                {
                    var package = new ZPackage(Utils.Decompress(raw));
                    if (package.ReadInt() != 1) throw new InvalidOperationException("Unknown saved terrain format; northern grave recovery is skipped.");
                    package.ReadInt(); package.ReadVector3(); package.ReadSingle();
                    int count = package.ReadInt();
                    int pitch = Mathf.RoundToInt(Mathf.Sqrt(count));
                    if (pitch < 2 || pitch * pitch != count) throw new InvalidOperationException("Invalid saved terrain grid; northern grave recovery is skipped.");
                    float scale = ZoneSystem.instance.m_zoneSize / (pitch - 1);
                    for (int index = 0; index < count; index++)
                    {
                        if (!package.ReadBool()) continue;
                        package.ReadSingle(); package.ReadSingle();
                        var vertex = data.GetPosition() + new Vector3(index % pitch - (pitch - 1) * 0.5f, 0f, index / pitch - (pitch - 1) * 0.5f) * scale;
                        if (FlatDistance(vertex, position) <= 3f) return true;
                    }
                }
                var prefab = ZNetScene.instance.GetPrefab(data.GetPrefab());
                var modifier = prefab == null ? null : prefab.GetComponent<TerrainModifier>();
                if (modifier != null && modifier.m_playerModifiction
                    && FlatDistance(data.GetPosition(), position) <= modifier.GetRadius() + 3f) return true;
            }
            return false;
        }

        // Recovery requires one compact, unmixed destruction batch with both refund Wood and native chest loot.
        // A timestamp alone does not establish provenance. Incomplete, merged or player-owned piles stay untouched.
        private static List<KeyValuePair<ZDO, ItemDrop.ItemData>> RecoverableSpill(Vector3 position, List<ZDO> nearby)
        {
            var drops = new List<KeyValuePair<ZDO, ItemDrop.ItemData>>();
            foreach (var data in nearby)
            {
                if (FlatDistance(data.GetPosition(), position) > 2f || data.GetByteArray(ZDOVars.s_itemData) == null) continue;
                var prefab = ObjectDB.instance.GetItemPrefab(data.GetPrefab());
                if (prefab == null || !prefab.TryGetComponent<ItemDrop>(out var drop)) return null;
                var item = drop.m_itemData.Clone();
                ItemDrop.LoadFromZDO(item, data);
                if (item.m_pickedUp || item.m_crafterID != 0 || (data.GetOwner() != 0 && data.GetOwner() != ZDOMan.GetSessionID())) return null;
                drops.Add(new KeyValuePair<ZDO, ItemDrop.ItemData>(data, item));
            }
            if (drops.Count < 3 || drops.Select(pair => pair.Key.GetLong(ZDOVars.s_spawnTime)).Distinct().Count() != 1
                || drops[0].Key.GetLong(ZDOVars.s_spawnTime) == 0) return null;
            var wood = drops.Where(pair => pair.Key.GetPrefab() == "Wood".GetStableHashCode()).ToList();
            var torches = drops.Where(pair => pair.Key.GetPrefab() == "Torch".GetStableHashCode()).ToList();
            if (wood.Count != 1 || wood[0].Value.m_stack != 3 || torches.Count != 1 || torches[0].Value.m_stack != 1) return null;
            var nativeLoot = new[] { "Coins", "Feathers", "ArrowCharred", "BoltCharred" }.Select(name => name.GetStableHashCode()).ToHashSet();
            if (!drops.Any(pair => nativeLoot.Contains(pair.Key.GetPrefab()))
                || drops.GroupBy(pair => pair.Key.GetPrefab()).Any(group => group.Count() > 1)) return null;
            if (drops.Any(pair => pair.Key.GetPrefab() != "Wood".GetStableHashCode() && pair.Key.GetPrefab() != "Torch".GetStableHashCode()
                && !nativeLoot.Contains(pair.Key.GetPrefab()) && !pair.Value.IsMagic())) return null;
            // Even a valid-looking batch is ambiguous if more objects with its timestamp lie just outside the pile.
            long timestamp = drops[0].Key.GetLong(ZDOVars.s_spawnTime);
            if (nearby.Any(data => FlatDistance(data.GetPosition(), position) <= 8f && data.GetByteArray(ZDOVars.s_itemData) != null
                && data.GetLong(ZDOVars.s_spawnTime) == timestamp && !drops.Any(pair => pair.Key == data))) return null;
            return drops.Where(pair => pair.Key.GetPrefab() != "Wood".GetStableHashCode()).ToList();
        }

        // A one-time recovery of eligible spills only. Missing chests alone are not evidence of accidental collapse.
        // Excavations, player belongings and ambiguous or incomplete piles retain every existing world object.
        internal static int Restore(ZDO proxy)
        {
            if (proxy.GetBool(RecoveryKey)) return 0;
            var slots = OriginalSlots(proxy);
            var nearby = Nearby(proxy.GetPosition());
            var restored = new List<ZDO>();
            var moved = new List<ZDO>();
            bool committed = false;
            var chestPrefab = ZNetScene.instance.GetPrefab(ChestPrefab);
            if (chestPrefab == null) throw new InvalidOperationException("Northern grave chest prefab is missing.");
            var container = chestPrefab.GetComponent<Container>();
            int objectsMoved = 0;
            try
            {
                foreach (var slot in slots)
                {
                    if (nearby.Any(data => data.GetPrefab() == ChestPrefab && FlatDistance(data.GetPosition(), slot.Position) < 1f)) continue;
                    // Rebuilding a loot container must not place it inside someone's stored belongings or structures.
                    if (nearby.Any(data => (data.GetLong(ZDOVars.s_creator) != 0 || PlayerGraves.IsPlayerGrave(data)
                        || data.GetBool(ZDOVars.s_tamed)) && FlatDistance(data.GetPosition(), slot.Position) < 8f)) continue;
                    if (nearby.Any(data => data != proxy && IsGrave(data) && FlatDistance(data.GetPosition(), proxy.GetPosition()) < 48f)
                        || TerrainWasEdited(slot.Position, nearby)) continue;
                    var spill = RecoverableSpill(slot.Position, nearby);
                    if (spill == null) continue;
                    var inventory = new Inventory(container.m_name, container.m_bkg, container.m_width, container.m_height);
                    foreach (var pair in spill)
                    {
                        if (moved.Contains(pair.Key)) throw new InvalidOperationException("Overlapping northern grave spills are ambiguous; source items remain in the world.");
                        if (!inventory.AddItem(pair.Value.Clone()))
                            throw new InvalidOperationException("Northern grave spill did not fit its original chest; source items remain in the world.");
                        moved.Add(pair.Key);
                    }
                    var point = slot.Position;
                    if (!ZoneSystem.instance.GetGroundHeight(point, out float groundHeight))
                        throw new InvalidOperationException("Northern grave recovery requires its loaded terrain; source items remain in the world.");
                    point.y = groundHeight - 2f;
                    var created = ZDOMan.instance.CreateNewZDO(point, ChestPrefab);
                    restored.Add(created);
                    created.SetPrefab(ChestPrefab);
                    var view = chestPrefab.GetComponent<ZNetView>();
                    created.Persistent = view.m_persistent;
                    created.Distant = view.m_distant;
                    created.Type = view.m_type;
                    created.SetOwner(ZDOMan.GetSessionID());
                    created.SetRotation(slot.Rotation);
                    created.Set(ZDOVars.s_addedDefaultItems, true);
                    created.Set("EpicLoot.ChestLootPending", false);
                    var package = new ZPackage();
                    inventory.Save(package);
                    created.Set(ZDOVars.s_items, package.GetArray());
                }
                // Commit containers before removing their source drops. Creation failures leave every source intact.
                committed = true;
                foreach (var data in moved)
                {
                    var instance = ZNetScene.instance.FindInstance(data.m_uid);
                    data.SetOwner(ZDOMan.GetSessionID());
                    if (instance != null) ZNetScene.instance.Destroy(instance);
                    else ZDOMan.instance.DestroyZDO(data);
                    objectsMoved++;
                }
                proxy.Set(RecoveryKey, true);
                if (restored.Count > 0) logger?.LogInfo($"Recovered {restored.Count} northern grave chests at {proxy.GetPosition():F1}, preserving {objectsMoved} uncollected item objects; loot rolls remain consumed.");
                return restored.Count;
            }
            catch
            {
                if (!committed) foreach (var data in restored) if (data.IsValid()) ZDOMan.instance.DestroyZDO(data);
                throw;
            }
        }

        internal static void OnLoaded(LocationProxy instance, bool spawned)
        {
            if (!spawned || ZNet.instance == null) return;
            var proxy = instance.GetComponent<ZNetView>().GetZDO();
            if (proxy == null || !IsGrave(proxy) || proxy.GetBool(RecoveryKey) || Failed.Contains(proxy.m_uid)) return;
            if (ZNet.instance.IsServer()) QueueRecovery(proxy);
            else ZNet.instance.GetServerRPC()?.Invoke(RecoveryRequestRpc, proxy.m_uid.UserID, unchecked((int)proxy.m_uid.ID));
        }

        internal static bool RequestAllowed(ZNetPeer peer, ZDO proxy) => peer != null && peer.IsReady() && proxy != null
            && proxy.IsValid() && IsGrave(proxy) && FlatDistance(peer.m_refPos, proxy.GetPosition()) <= 384f;

        internal static void Register(ZNet network, ZNetPeer peer)
        {
            if (!network.IsServer()) return;
            peer.m_rpc.Register<long, int>(RecoveryRequestRpc, (rpc, user, id) =>
            {
                var proxy = ZDOMan.instance?.GetZDO(new ZDOID(user, unchecked((uint)id)));
                if (RequestAllowed(peer, proxy)) QueueRecovery(proxy);
            });
        }

        private static void QueueRecovery(ZDO proxy)
        {
            if (proxy.GetBool(RecoveryKey) || Failed.Contains(proxy.m_uid) || !Pending.Add(proxy.m_uid)) return;
            runner.StartCoroutine(RecoverLoaded(proxy));
        }

        private static IEnumerator RecoverLoaded(ZDO proxy)
        {
            var location = GetLocation(ZoneSystem.instance, proxy.GetInt(ZDOVars.s_location));
            if (location == null)
            {
                Pending.Remove(proxy.m_uid);
                throw new InvalidOperationException("Requested northern grave definition is missing.");
            }
            location.m_prefab.Load();
            try
            {
                float deadline = Time.realtimeSinceStartup + 30f;
                while (!location.m_prefab.IsLoaded)
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        Failed.Add(proxy.m_uid);
                        logger.LogError($"Northern grave recovery at {proxy.GetPosition()} could not load its original layout and remains pending until reload.");
                        yield break;
                    }
                    yield return null;
                }
                if (!proxy.IsValid()) yield break;
                var slots = OriginalSlots(proxy);
                var zones = slots.Select(slot => ZoneSystem.GetZone(slot.Position)).Distinct().ToArray();
                while (slots.Any(slot => !ZoneSystem.instance.GetGroundHeight(slot.Position, out _)))
                {
                    foreach (var zone in zones) LoadZone(ZoneSystem.instance, zone);
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        Failed.Add(proxy.m_uid);
                        logger.LogError($"Northern grave recovery at {proxy.GetPosition()} could not load its terrain and remains pending until reload.");
                        yield break;
                    }
                    yield return null;
                }
                // Native SnapToGround finishes queued terrain deformation before positioning buried objects.
                Heightmap.ForceGenerateAll();
                try { Restore(proxy); }
                catch (Exception error)
                {
                    Failed.Add(proxy.m_uid);
                    logger.LogError($"Northern grave recovery at {proxy.GetPosition()} failed and remains pending until reload: {error}");
                }
            }
            finally
            {
                location.m_prefab.Release();
                Pending.Remove(proxy.m_uid);
            }
        }

        internal static void ClearWorld() { Pending.Clear(); Failed.Clear(); }
    }

    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateWear))]
    internal static class NorthGraveChestSupport
    {
        // Every building piece runs this, so the cheap support flag and the game's own view come before any lookup.
        private static void Prefix(WearNTear __instance)
        {
            if (!__instance.m_noSupportWear) return;
            var view = GameFields.WearView(__instance);
            var data = view == null ? null : view.GetZDO();
            if (data != null && data.GetPrefab() == NorthGraveChests.ChestPrefab && NorthGraveChests.IsBuriedGraveChest(data))
                __instance.m_noSupportWear = false;
        }
    }

    [HarmonyPatch(typeof(LocationProxy), "SpawnLocation")]
    internal static class NorthGraveChestRecovery
    {
        private static void Postfix(LocationProxy __instance, bool __result) => NorthGraveChests.OnLoaded(__instance, __result);
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    internal static class NorthGraveRecoveryConnection
    {
        private static void Postfix(ZNet __instance, ZNetPeer peer) => NorthGraveChests.Register(__instance, peer);
    }

    [HarmonyPatch(typeof(ZNet), "Shutdown")]
    internal static class NorthGraveRecoveryShutdown
    {
        private static void Postfix() => NorthGraveChests.ClearWorld();
    }
}
