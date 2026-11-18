using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The game rolls a Forge of Potential refinement on the client from a seed it derives from the character's name and keeps
    // in the character file, so every coming result can be read in advance. A client on a server asks the server instead when
    // the crafting timer ends, and the server draws the roll with a cryptographic generator. A roll stays the account's for
    // that item and quality until a refinement uses it, so cancelling or asking again never draws a new one, and every roll
    // is written to saves/refinement-rolls/<world>.jsonl. Singleplayer and a hosting player keep the game's own roll.
    [HarmonyPatch]
    internal static class RefinementRolls
    {
        private const string RequestRpc = Plugin.Id + ".refinement-roll.request", RollRpc = Plugin.Id + ".refinement-roll",
            AppliedRpc = Plugin.Id + ".refinement-roll.applied";
        private const float ReplyTimeout = 10f, RequestInterval = 1f;
        private static readonly AccessTools.FieldRef<InventoryGui, Recipe> CraftRecipe = AccessTools.FieldRefAccess<InventoryGui, Recipe>("m_craftRecipe");
        private static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> CraftUpgrade = AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_craftUpgradeItem");
        // IsVisible stays true for a frame after the window closes; the animator's flag is what Hide clears.
        private static readonly AccessTools.FieldRef<InventoryGui, Animator> Window = AccessTools.FieldRefAccess<InventoryGui, Animator>("m_animator");
        private static readonly Action<InventoryGui, Player> DoCrafting = AccessTools.MethodDelegate<Action<InventoryGui, Player>>(AccessTools.Method(typeof(InventoryGui), "DoCrafting"));
        private static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();

        // Client: the request waiting for its roll, and the roll the next native refinement uses.
        private sealed class Request
        {
            internal string Item;
            internal int Quality;
            internal float Sent;
        }
        private static Request waiting;
        private static float? granted;
        private static bool used;

        // Server: unused rolls by account, item and quality, read back from the world's file.
        private sealed class Entry
        {
            public string At, Event, Id, Account, Character, Item;
            public int Quality;
            public float Roll;
        }
        private static readonly Dictionary<string, Entry> unused = new Dictionary<string, Entry>();
        private static readonly Dictionary<string, float> lastRequest = new Dictionary<string, float>();
        private static long loadedWorld;
        private static string journal;

        internal const string NoAnswer = "The server didn't decide this refinement, so nothing was refined. Try again.";
        internal const string Cancelled = "Nothing was refined. Stay at the forge until the refinement finishes.";

        private static bool ServerDecides(InventoryGui gui)
        {
            var station = Player.m_localPlayer ? Player.m_localPlayer.GetCurrentCraftingStation() : null;
            return ZNet.instance != null && !ZNet.instance.IsServer() && ZNet.instance.GetServerPeer() != null &&
                station && station.m_upgrader && CraftRecipe(gui) != null && CraftUpgrade(gui) != null;
        }

        // The crafting timer's one call of DoCrafting.
        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> AskAtTimerEnd(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(AccessTools.Method(typeof(InventoryGui), "DoCrafting")),
                AccessTools.Method(typeof(RefinementRolls), nameof(Craft)), 1, "InventoryGui.UpdateRecipe no longer finishes crafting with one DoCrafting call.");

        private static void Craft(InventoryGui gui, Player player)
        {
            if (!ServerDecides(gui)) { DoCrafting(gui, player); return; }
            if (waiting != null) return;
            waiting = new Request { Item = CraftRecipe(gui).m_item.gameObject.name, Quality = CraftUpgrade(gui).m_quality + 1, Sent = Time.realtimeSinceStartup };
            var package = new ZPackage();
            package.Write(waiting.Item);
            package.Write(waiting.Quality);
            ZNet.instance.GetServerPeer().m_rpc.Invoke(RequestRpc, package);
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetItemUpgradeChance)), HarmonyPrefix]
        private static bool UseGrantedRoll(ref float __result)
        {
            if (granted == null) return true;
            __result = granted.Value;
            granted = null;
            used = true;
            return false;
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPostfix]
        private static void Register(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer())
            {
                peer.m_rpc.Register<ZPackage>(RequestRpc, Decide);
                peer.m_rpc.Register<string>(AppliedRpc, Applied);
            }
            else if (peer.m_server) peer.m_rpc.Register<ZPackage>(RollRpc, Receive);
        }

        internal static void Tick()
        {
            if (waiting == null || ZNet.instance != null && ZNet.instance.GetServerPeer() != null && Time.realtimeSinceStartup - waiting.Sent < ReplyTimeout) return;
            waiting = null;
            if (Player.m_localPlayer) Player.m_localPlayer.Message(MessageHud.MessageType.Center, NoAnswer);
        }

        private static void Receive(ZRpc rpc, ZPackage package)
        {
            if (ZNet.instance == null || ZNet.instance.GetServerPeer()?.m_rpc != rpc) return;
            string id = package.ReadString(), item = package.ReadString();
            int quality = package.ReadInt();
            float roll = package.ReadSingle();
            var request = waiting;
            if (request == null || request.Item != item || request.Quality != quality) return;
            waiting = null;
            var gui = InventoryGui.instance;
            var player = Player.m_localPlayer;
            if (!player) return;
            if (id.Length == 0) { player.Message(MessageHud.MessageType.Center, NoAnswer); return; }
            // The player may have left the forge or chosen something else meanwhile; the server keeps the roll for the next attempt.
            var upgrade = gui ? CraftUpgrade(gui) : null;
            if (!gui || !Window(gui).GetBool("visible") || !ServerDecides(gui) || CraftRecipe(gui).m_item.gameObject.name != item || upgrade.m_quality + 1 != quality ||
                !player.GetInventory().ContainsItem(upgrade))
            {
                player.Message(MessageHud.MessageType.Center, Cancelled);
                return;
            }
            granted = roll;
            used = false;
            try { DoCrafting(gui, player); }
            finally { granted = null; }
            if (used) rpc.Invoke(AppliedRpc, id);
        }

        private static void Decide(ZRpc rpc, ZPackage package)
        {
            string item = package.ReadString();
            int quality = package.ReadInt();
            var peer = ZNet.instance.GetPeers().Find(candidate => candidate.m_rpc == rpc);
            var reply = new ZPackage();
            string id = "";
            float roll = 0f;
            float now = Time.realtimeSinceStartup;
            try
            {
                if (peer == null || !peer.IsReady()) return;
                string account = peer.m_socket.GetHostName();
                if (lastRequest.TryGetValue(account, out float last) && now - last < RequestInterval) throw new InvalidDataException("requests arrived too quickly");
                lastRequest[account] = now;
                if (item == null || item.Length > 80 || ObjectDB.instance.GetItemPrefab(item) == null || quality < 2 || quality > 1000)
                    throw new InvalidDataException("unknown item " + item + " or quality " + quality);
                string key = account + "|" + item + "|" + quality.ToString(CultureInfo.InvariantCulture);
                Load();
                if (unused.TryGetValue(key, out var kept))
                    Write(new Entry { Event = "reused", Id = kept.Id, Account = account, Character = peer.m_playerName, Item = item, Quality = quality, Roll = kept.Roll });
                else
                {
                    var bytes = new byte[4];
                    Random.GetBytes(bytes);
                    // Like UnityEngine.Random.value, from 0 to 1 inclusive.
                    kept = new Entry { Event = "issued", Id = Guid.NewGuid().ToString("N"), Account = account, Character = peer.m_playerName, Item = item, Quality = quality,
                        Roll = (BitConverter.ToUInt32(bytes, 0) & 0xFFFFFF) / 16777215f };
                    Write(kept);
                    unused[key] = kept;
                }
                id = kept.Id; roll = kept.Roll;
            }
            catch (Exception error) { Debug.LogError("Progression Fix refinement roll for " + peer?.m_playerName + ": " + error.GetBaseException().Message); }
            reply.Write(id);
            reply.Write(item ?? "");
            reply.Write(quality);
            reply.Write(roll);
            rpc.Invoke(RollRpc, reply);
        }

        private static void Applied(ZRpc rpc, string id)
        {
            var peer = ZNet.instance.GetPeers().Find(candidate => candidate.m_rpc == rpc);
            try
            {
                if (peer == null) return;
                Load();
                var key = unused.FirstOrDefault(pair => pair.Value.Id == id && pair.Value.Account == peer.m_socket.GetHostName()).Key;
                if (key == null) throw new InvalidDataException("no unused roll " + id);
                var entry = unused[key];
                Write(new Entry { Event = "applied", Id = id, Account = entry.Account, Character = peer.m_playerName, Item = entry.Item, Quality = entry.Quality, Roll = entry.Roll });
                unused.Remove(key);
            }
            catch (Exception error) { Debug.LogError("Progression Fix refinement roll for " + peer?.m_playerName + ": " + error.GetBaseException().Message); }
        }

        // Each world's rolls, read once: issued and reused ones stay unused until applied.
        private static void Load()
        {
            long world = ZNet.instance.GetWorldUID();
            if (journal != null && world == loadedWorld) return;
            unused.Clear();
            string path = Path.Combine(global::Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "refinement-rolls", world.ToString(CultureInfo.InvariantCulture) + ".jsonl");
            if (File.Exists(path))
                foreach (string line in File.ReadAllLines(path).Where(line => line.Length > 0))
                {
                    var entry = JsonConvert.DeserializeObject<Entry>(line);
                    string key = entry.Account + "|" + entry.Item + "|" + entry.Quality.ToString(CultureInfo.InvariantCulture);
                    if (entry.Event == "applied") unused.Remove(key);
                    else unused[key] = entry;
                }
            journal = path;
            loadedWorld = world;
        }

        // Written before the roll is sent, so no roll reaches a player without a record.
        private static void Write(Entry entry)
        {
            entry.At = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Directory.CreateDirectory(Path.GetDirectoryName(journal));
            File.AppendAllText(journal, JsonConvert.SerializeObject(entry) + "\n");
        }
    }
}
