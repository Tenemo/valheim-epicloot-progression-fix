using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Each player takes the Dyrnwyn tip and blade from their pickups once. The pickups stay in the world for everyone, and
    // those the game removed when taken before this come back. The champion returns a day after he died or left, and his
    // death drops one hilt for each player near him who never had one dropped. The server keeps, per world, which accounts
    // took the tip and blade and had a hilt dropped; a world without records starts from the item journal, so fragments
    // taken before this keep counting. Fragments on the ground or passed between players are ordinary items.
    [HarmonyPatch]
    internal static class DyrnwynFragments
    {
        private const string TakeRpc = "EpicLootProgressionFix_DyrnwynTake";
        private const string GrantRpc = "EpicLootProgressionFix_DyrnwynGrant";
        private const string ChampionDiedRpc = "EpicLootProgressionFix_DyrnwynChampionDied";
        private const string RestoreRpc = "EpicLootProgressionFix_DyrnwynRestorePickup";
        private const float PickupReach = 10f;
        private const float RestoreReach = 300f;
        internal const string AlreadyTaken = "You already took this fragment.";

        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Dyrnwyn fragments");

        // Prefab names, from the game's own sources of each fragment.
        private static readonly Dictionary<string, string> PickupFragment = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Champions = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> ChampionSpawners = new(StringComparer.Ordinal);

        private static DyrnwynFragmentRecords records;
        private static long recordsWorld;
        private static readonly HashSet<ZDOID> HandledKills = new();
        private static readonly Dictionary<ZDOID, float> LastRequest = new();

        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void FindSources(ZNetScene __instance)
        {
            PickupFragment.Clear(); Champions.Clear(); ChampionSpawners.Clear();
            foreach (var prefab in __instance.m_prefabs)
            {
                if (prefab == null) continue;
                var pickable = prefab.GetComponent<Pickable>();
                string item = pickable != null && pickable.m_itemPrefab != null ? pickable.m_itemPrefab.name : null;
                if (item == DyrnwynFragmentRecords.Tip || item == DyrnwynFragmentRecords.Blade) PickupFragment[prefab.name] = item;
                var drops = prefab.GetComponent<CharacterDrop>();
                if (drops != null && drops.m_drops.Any(drop => drop.m_prefab != null && drop.m_prefab.name == DyrnwynFragmentRecords.Hilt))
                    Champions.Add(prefab.name);
            }
            foreach (var prefab in __instance.m_prefabs)
            {
                var spawner = prefab != null ? prefab.GetComponent<CreatureSpawner>() : null;
                if (spawner != null && spawner.m_creaturePrefab != null && Champions.Contains(spawner.m_creaturePrefab.name))
                    ChampionSpawners[prefab.name] = spawner.m_triggerDistance;
            }
            if (!PickupFragment.ContainsValue(DyrnwynFragmentRecords.Tip) || !PickupFragment.ContainsValue(DyrnwynFragmentRecords.Blade) ||
                Champions.Count == 0 || ChampionSpawners.Count == 0)
                throw new InvalidOperationException("The Dyrnwyn fragment pickups, champion or champion spawner are missing.");
        }

        private static string Name(GameObject gameObject) => Utils.GetPrefabName(gameObject);

        private static bool IsFragmentPickup(Pickable pickable) => pickable.m_itemPrefab != null &&
            (pickable.m_itemPrefab.name == DyrnwynFragmentRecords.Tip || pickable.m_itemPrefab.name == DyrnwynFragmentRecords.Blade);

        [HarmonyPatch]
        private static class Registration
        {
            private static MethodBase TargetMethod() => AccessTools.Constructor(typeof(ZRoutedRpc), new[] { typeof(bool) });
            private static void Postfix(ZRoutedRpc __instance)
            {
                __instance.Register<ZDOID>(TakeRpc, ReceiveTake);
                __instance.Register<ZDOID>(GrantRpc, ReceiveGrant);
                __instance.Register<ZDOID, Vector3>(ChampionDiedRpc, ReceiveChampionDied);
                __instance.Register<ZDOID>(RestoreRpc, ReceiveRestore);
            }
        }

        private static void ToServer(string method, params object[] parameters)
        {
            if (ZNet.instance == null || ZRoutedRpc.instance == null) return;
            if (ZNet.instance.IsServer()) ZRoutedRpc.instance.InvokeRoutedRPC(ZDOMan.GetSessionID(), method, parameters);
            else if (ZNet.instance.GetServerPeer() is ZNetPeer server) ZRoutedRpc.instance.InvokeRoutedRPC(server.m_uid, method, parameters);
        }

        private static bool FromServer(long sender) =>
            ZNet.instance != null && (ZNet.instance.IsServer() ? sender == ZDOMan.GetSessionID() : ZNet.instance.GetServerPeer()?.m_uid == sender);

        // Server side.

        private static DyrnwynFragmentRecords Records()
        {
            long world = ZNet.instance.GetWorldUID();
            if (records != null && recordsWorld == world) return records;
            records = null;
            string path = RecordsPath(world);
            if (File.Exists(path)) records = DyrnwynFragmentRecords.Parse(File.ReadAllText(path));
            else
            {
                string journal = Path.Combine(SaveDirectory, "item-activity", world.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var lines = Directory.Exists(journal)
                    ? Directory.EnumerateFiles(journal, "*.jsonl", SearchOption.AllDirectories).OrderBy(file => file, StringComparer.Ordinal).SelectMany(File.ReadLines)
                    : Enumerable.Empty<string>();
                records = DyrnwynFragmentRecords.FromJournal(lines, out int unreadable);
                Save(path, records);
                Log.LogInfo($"Dyrnwyn fragment records started from the item journal: {records.Count(DyrnwynFragmentRecords.Tip)} tip, " +
                    $"{records.Count(DyrnwynFragmentRecords.Blade)} blade and {records.Count(DyrnwynFragmentRecords.Hilt)} hilt" +
                    (unreadable > 0 ? $"; {unreadable} unreadable journal lines skipped." : "."));
            }
            recordsWorld = world;
            return records;
        }

        private static string SaveDirectory => CharacterCheckpoints.Available ? CharacterCheckpoints.PrivateSaveDirectory() :
            global::Utils.GetSaveDataPath(FileHelpers.FileSource.Local);

        private static string RecordsPath(long world) => Path.Combine(SaveDirectory, "dyrnwyn-fragments",
            world.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");

        private static void Save(string path, DyrnwynFragmentRecords saved)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WholeFile.Replace(path, System.Text.Encoding.UTF8.GetBytes(saved.Serialize()));
        }

        // The grant and its physical item share a world save. A missed pickup notification leaves the item on the ground.
        // Keep recognizing the grants earlier releases recorded in the private file.
        private static ZDOID DropOnce(string fragment, string account, Vector3 position)
        {
            using var sha = SHA256.Create();
            string key = "dyrnwyn_" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                ZNet.instance.GetWorldUID() + "|" + fragment + "|" + account))).Replace("-", "").ToLowerInvariant();
            if (Records().Has(fragment, account) || ZoneSystem.instance.GetGlobalKey(key)) return ZDOID.None;
            var prefab = ObjectDB.instance.GetItemPrefab(fragment);
            var drop = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity).GetComponent<ItemDrop>();
            ItemDrop.OnCreateNew(drop);
            var view = drop.GetComponent<ZNetView>();
            ItemDrop.SaveToZDO(drop.m_itemData, view.GetZDO());
            ZoneSystem.instance.SetGlobalKey(key);
            return view.GetZDO().m_uid;
        }

        private static bool Collector(long sender, out string account, out Vector3 position)
        {
            account = null; position = default;
            if (sender == ZDOMan.GetSessionID() && Player.m_localPlayer)
            {
                account = UserInfo.GetLocalUser().UserId.ToString();
                position = Player.m_localPlayer.transform.position;
                return true;
            }
            var peer = ZNet.instance.GetPeer(sender);
            if (peer == null || !peer.IsReady() || !peer.m_rpc.IsConnected()) return false;
            position = peer.m_refPos;
            if (CharacterCheckpoints.Available)
                return CharacterCheckpoints.PrivatePeerIdentity(peer.m_rpc, out account, out _, out _);
            account = peer.m_socket.GetHostName();
            if (string.IsNullOrEmpty(account)) return false;
            if (account.All(char.IsDigit)) account = "Steam_" + account;
            return true;
        }

        private static void Server(string action, Action run)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZNetScene.instance == null || !Plugin.Ready) return;
            try { run(); }
            catch (Exception error) { Log.LogError(action + " failed: " + error.GetBaseException().Message); }
        }

        private static void ReceiveTake(long sender, ZDOID pickupId) => Server("A Dyrnwyn fragment pickup", () =>
        {
            var pickup = ZDOMan.instance.GetZDO(pickupId);
            var prefab = pickup != null ? ZNetScene.instance.GetPrefab(pickup.GetPrefab()) : null;
            if (prefab == null || !PickupFragment.TryGetValue(prefab.name, out string fragment) || !Collector(sender, out string account, out var position) ||
                Vector3.Distance(position, pickup.GetPosition()) > PickupReach) return;
            var item = DropOnce(fragment, account, position + Vector3.up);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, GrantRpc, item);
        });

        // The champion's owner reports his death while his record still exists, and drops one hilt for each player near him
        // who never had one dropped. Who picks them up is up to the players.
        private static void ReceiveChampionDied(long sender, ZDOID championId, Vector3 center) => Server("The Dyrnwyn champion's death", () =>
        {
            var champion = ZDOMan.instance.GetZDO(championId);
            var prefab = champion != null ? ZNetScene.instance.GetPrefab(champion.GetPrefab()) : null;
            if (prefab == null || !Champions.Contains(prefab.name) || champion.GetOwner() != sender || !HandledKills.Add(championId)) return;
            float range = ChampionSpawners.Values.Max();
            var position = champion.GetPosition();
            foreach (long id in ZNet.instance.GetPeers().Where(peer => peer.IsReady()).Select(peer => peer.m_uid).Concat(new[] { ZDOMan.GetSessionID() }))
            {
                if (Collector(id, out var account, out var playerPosition) && Vector3.Distance(playerPosition, position) <= range)
                    DropOnce(DyrnwynFragmentRecords.Hilt, account, position + Vector3.up);
            }
        });

        private static void ReceiveGrant(long sender, ZDOID drop)
        {
            var player = Player.m_localPlayer;
            if (!FromServer(sender) || !player) return;
            if (drop.IsNone()) player.Message(MessageHud.MessageType.Center, AlreadyTaken);
            else player.StartCoroutine(HarvestOutputPickup.Pickup(player, new List<ZDOID> { drop }));
        }

        [HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact)), HarmonyPrefix]
        private static bool TakeFragment(Pickable __instance, ZNetView ___m_nview, bool repeat, ref bool __result)
        {
            if (!IsFragmentPickup(__instance)) return true;
            __result = false;
            if (repeat || !___m_nview.IsValid() || __instance.GetEnabled == 0) return false;
            var id = ___m_nview.GetZDO().m_uid;
            if (LastRequest.TryGetValue(id, out float last) && Time.time - last < 1f) return false;
            LastRequest[id] = Time.time;
            ToServer(TakeRpc, id);
            return false;
        }

        // The pickup itself is never taken, so it keeps showing its fragment for the players who still need it.
        [HarmonyPatch(typeof(Pickable), "RPC_Pick"), HarmonyPrefix]
        private static bool KeepPickup(Pickable __instance) => !IsFragmentPickup(__instance);

        // The game removed a natively taken tip or blade pickup for good. A player's game draws a site with each of its
        // objects in place but unused, so for each fragment pickup there the server puts back a pickup that is gone.
        // Nothing else in the site changes.
        [HarmonyPatch(typeof(LocationProxy), "SpawnLocation"), HarmonyPostfix]
        private static void FindMissingPickups(LocationProxy __instance, bool __result, GameObject ___m_instance)
        {
            if (__result && ___m_instance && Plugin.Ready && ZNet.instance && !ZNet.instance.IsDedicated() &&
                ___m_instance.GetComponentsInChildren<Pickable>(true).Any(IsFragmentPickup))
                ToServer(RestoreRpc, __instance.GetComponent<ZNetView>().GetZDO().m_uid);
        }

        private static void ReceiveRestore(long sender, ZDOID proxyId) => Server("Putting back a Dyrnwyn fragment pickup", () =>
        {
            var proxy = ZDOMan.instance.GetZDO(proxyId);
            if (proxy == null || proxy.GetPrefab() != ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode() ||
                !Collector(sender, out _, out var playerPosition)) return;
            var origin = proxy.GetPosition();
            if (Utils.DistanceXZ(playerPosition, origin) > RestoreReach ||
                !DungeonInteriors.Locations(ZoneSystem.instance).TryGetValue(ZoneSystem.GetZone(origin), out var placed) ||
                !placed.m_placed || placed.m_location.Hash != proxy.GetInt(ZDOVars.s_location) ||
                Utils.DistanceXZ(placed.m_position, origin) > 1f) return;
            var sector = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(origin), new SimulationDistance(1, 0), sector);
            // Refuse an ambiguous proxy. The real generated site has one proxy at the recorded position.
            if (sector.Count(data => data.GetPrefab() == proxy.GetPrefab() && data.GetInt(ZDOVars.s_location) == placed.m_location.Hash &&
                Utils.DistanceXZ(data.GetPosition(), placed.m_position) <= 1f) != 1) return;
            var asset = placed.m_location.m_prefab;
            asset.Load();
            try
            {
                var root = asset.Asset;
                foreach (var pickable in root.GetComponentsInChildren<Pickable>(true).Where(IsFragmentPickup))
                {
                    string name = Name(pickable.gameObject);
                    if (!PickupFragment.ContainsKey(name)) name = PickupFragment.First(pair => pair.Value == pickable.m_itemPrefab.name).Key;
                    var matrix = SummonDungeonTravel.LocationMatrix(pickable.transform, root.transform, root.GetComponent<Location>(),
                        origin, proxy.GetRotation(), out var rotation);
                    Vector3 position = matrix.GetColumn(3);
                    var nearby = new List<ZDO>();
                    ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(position), new SimulationDistance(1, 0), nearby);
                    int hash = name.GetStableHashCode();
                    if (nearby.Any(data => data.GetPrefab() == hash && Vector3.Distance(data.GetPosition(), position) < 2f)) continue;
                    var view = ZNetScene.instance.GetPrefab(hash).GetComponent<ZNetView>();
                    var created = ZDOMan.instance.CreateNewZDO(position, hash);
                    created.SetPrefab(hash);
                    created.Persistent = view.m_persistent;
                    created.Type = view.m_type;
                    created.Distant = view.m_distant;
                    created.SetRotation(rotation);
                }
            }
            finally { asset.Release(); }
        });

        [HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void RemoveNativeHilt(List<KeyValuePair<GameObject, int>> __result) =>
            __result?.RemoveAll(drop => drop.Key != null && drop.Key.name == DyrnwynFragmentRecords.Hilt);

        [HarmonyPatch(typeof(CharacterDrop), "OnDeath"), HarmonyPrefix]
        private static void ReportChampionDeath(CharacterDrop __instance, bool ___m_dropsEnabled)
        {
            var view = __instance.GetComponent<ZNetView>();
            if (!___m_dropsEnabled || view == null || !view.IsValid() || !Champions.Contains(Name(__instance.gameObject))) return;
            var character = __instance.GetComponent<Character>();
            ToServer(ChampionDiedRpc, view.GetZDO().m_uid, character != null ? character.GetCenterPoint() : __instance.transform.position + Vector3.up);
        }

        // The game places the champion once. He returns a day after he was last seen alive, through the spawner's own timer;
        // this runs after the one-shot setting for dungeon interior spawners.
        [HarmonyPatch(typeof(CreatureSpawner), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void ReturnDaily(CreatureSpawner __instance)
        {
            if (ChampionSpawners.ContainsKey(Name(__instance.gameObject))) __instance.m_respawnTimeMinuts = EnvMan.instance.m_dayLengthSec / 60f;
        }

        internal static bool ReturnsDaily(string spawner) => ChampionSpawners.ContainsKey(spawner);
    }
}
