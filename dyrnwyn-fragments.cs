using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        private const string DropHiltsRpc = "EpicLootProgressionFix_DyrnwynDropHilts";
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
                __instance.Register<string, bool>(GrantRpc, ReceiveGrant);
                __instance.Register<ZDOID, Vector3>(ChampionDiedRpc, ReceiveChampionDied);
                __instance.Register<int, Vector3>(DropHiltsRpc, ReceiveDropHilts);
                __instance.Register<string, Vector3, Quaternion>(RestoreRpc, ReceiveRestore);
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
                string journal = ItemActivityChannel.JournalDirectory;
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

        private static string RecordsPath(long world) => Path.Combine(CharacterCheckpoints.PrivateSaveDirectory(), "dyrnwyn-fragments",
            world.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");

        private static void Save(string path, DyrnwynFragmentRecords saved)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WholeFile.Replace(path, System.Text.Encoding.UTF8.GetBytes(saved.Serialize()));
        }

        // Records an account's fragment and saves it before the fragment is handed over.
        private static bool Record(string fragment, string account)
        {
            var current = Records();
            if (!current.Add(fragment, account)) return false;
            try { Save(RecordsPath(recordsWorld), current); }
            catch { records = null; throw; }
            return true;
        }

        private static bool Account(long sender, out ZNetPeer peer, out string account)
        {
            account = null;
            peer = ZNet.instance.GetPeer(sender);
            return peer != null && CharacterCheckpoints.PrivatePeerIdentity(peer.m_rpc, out account, out _, out _);
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
            if (prefab == null || !PickupFragment.TryGetValue(prefab.name, out string fragment) || !Account(sender, out var peer, out string account) ||
                Vector3.Distance(peer.m_refPos, pickup.GetPosition()) > PickupReach) return;
            bool granted = Record(fragment, account);
            if (granted) Log.LogInfo($"{peer.m_playerName} took the Dyrnwyn fragment {fragment}.");
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, GrantRpc, fragment, granted);
        });

        // The champion's owner reports his death while his record still exists, and drops one hilt for each player near him
        // who never had one dropped. Who picks them up is up to the players.
        private static void ReceiveChampionDied(long sender, ZDOID championId, Vector3 center) => Server("The Dyrnwyn champion's death", () =>
        {
            var champion = ZDOMan.instance.GetZDO(championId);
            var prefab = champion != null ? ZNetScene.instance.GetPrefab(champion.GetPrefab()) : null;
            if (prefab == null || !Champions.Contains(prefab.name) || champion.GetOwner() != sender || !HandledKills.Add(championId)) return;
            float range = ChampionSpawners.Values.Max();
            int hilts = 0;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady() || Vector3.Distance(peer.m_refPos, champion.GetPosition()) > range ||
                    !CharacterCheckpoints.PrivatePeerIdentity(peer.m_rpc, out string account, out _, out _) ||
                    !Record(DyrnwynFragmentRecords.Hilt, account)) continue;
                Log.LogInfo($"The Dyrnwyn champion dropped a hilt for {peer.m_playerName}.");
                hilts++;
            }
            if (hilts > 0) ZRoutedRpc.instance.InvokeRoutedRPC(sender, DropHiltsRpc, hilts, center);
        });

        // Player side.

        private static void ReceiveGrant(long sender, string fragment, bool granted)
        {
            var player = Player.m_localPlayer;
            if (!FromServer(sender) || player == null || !DyrnwynFragmentRecords.Fragments.Contains(fragment)) return;
            if (!granted) { player.Message(MessageHud.MessageType.Center, AlreadyTaken); return; }
            var prefab = ObjectDB.instance.GetItemPrefab(fragment);
            var item = prefab.GetComponent<ItemDrop>().m_itemData;
            if (player.GetInventory().CanAddItem(prefab, 1) &&
                player.GetInventory().AddItem(fragment, 1, 1, 0, 0L, "", false, pickedUp: true) != null)
                player.Message(MessageHud.MessageType.TopLeft, "$msg_added " + item.m_shared.m_name, 1, item.GetIcon());
            else
            {
                // A full inventory gets it at the player's feet, as the native pickup leaves an item it cannot carry.
                var drop = UnityEngine.Object.Instantiate(prefab, player.transform.position + Vector3.up, Quaternion.identity).GetComponent<ItemDrop>();
                ItemDrop.OnCreateNew(drop);
                player.Message(MessageHud.MessageType.Center, "$inventory_full");
            }
        }

        private static void ReceiveDropHilts(long sender, int hilts, Vector3 center)
        {
            if (!FromServer(sender) || hilts <= 0 || ObjectDB.instance == null) return;
            CharacterDrop.DropItems(new List<KeyValuePair<GameObject, int>> { new(ObjectDB.instance.GetItemPrefab(DyrnwynFragmentRecords.Hilt), hilts) },
                center, 0.5f, false);
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
        private static void FindMissingPickups(bool __result, GameObject ___m_instance)
        {
            if (!__result || ___m_instance == null || !Plugin.Ready || ZNet.instance == null || ZNet.instance.IsDedicated()) return;
            foreach (var pickable in ___m_instance.GetComponentsInChildren<Pickable>(true))
                if (IsFragmentPickup(pickable))
                    ToServer(RestoreRpc, pickable.m_itemPrefab.name, pickable.transform.position, pickable.transform.rotation);
        }

        private static void ReceiveRestore(long sender, string fragment, Vector3 position, Quaternion rotation) => Server("Putting back a Dyrnwyn fragment pickup", () =>
        {
            string pickup = PickupFragment.FirstOrDefault(entry => entry.Value == fragment).Key;
            if (pickup == null || !Account(sender, out var peer, out _) || Vector3.Distance(peer.m_refPos, position) > RestoreReach) return;
            int hash = pickup.GetStableHashCode();
            var nearby = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(position), new SimulationDistance(1, 0), nearby);
            if (nearby.Any(data => data.GetPrefab() == hash && Vector3.Distance(data.GetPosition(), position) < 2f)) return;
            var view = ZNetScene.instance.GetPrefab(hash).GetComponent<ZNetView>();
            var created = ZDOMan.instance.CreateNewZDO(position, hash);
            created.SetPrefab(hash);
            created.Persistent = view.m_persistent;
            created.Type = view.m_type;
            created.Distant = view.m_distant;
            created.SetRotation(rotation);
            Log.LogInfo($"Put back the {fragment} pickup {pickup} at {position.x:0},{position.z:0}.");
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
    }
}
