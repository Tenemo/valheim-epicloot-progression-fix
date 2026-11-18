using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
            AppliedRpc = Plugin.Id + ".refinement-roll.applied", SettledRpc = Plugin.Id + ".refinement-roll.settled";
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
            internal ItemDrop.ItemData Selected;
            internal CraftingStation Station;
            internal string Nonce;
            internal int Quality;
            internal float Sent;
        }
        private static Request waiting;
        private static float? granted;
        private static bool used;
        private const string ReceiptPrefix = Plugin.Id + ".refined.";
        private static readonly Dictionary<string, string> awaitingSettlement = new();
        private static float nextSettlement;

        // Server: unused rolls by account, item and quality, read back from the world's file.
        private sealed class Entry
        {
            public string At, Event, Id, Account, Character, Item;
            public int Quality;
            public float Roll;
        }
        private static Dictionary<string, Entry> unused = new Dictionary<string, Entry>();
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
            waiting = new Request { Item = CraftRecipe(gui).m_item.gameObject.name, Quality = CraftUpgrade(gui).m_quality + 1,
                Selected = CraftUpgrade(gui), Station = player.GetCurrentCraftingStation(), Nonce = Guid.NewGuid().ToString("N"), Sent = Time.realtimeSinceStartup };
            var package = new ZPackage();
            package.Write(waiting.Item);
            package.Write(waiting.Quality);
            package.Write(waiting.Nonce);
            player.m_customData.TryGetValue(ReceiptKey(waiting.Item, waiting.Quality), out string previous);
            package.Write(previous ?? "");
            if (!CharacterCheckpoints.Available && previous != null) PublishReceipt(player, ReceiptKey(waiting.Item, waiting.Quality), previous);
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
            else if (peer.m_server)
            {
                peer.m_rpc.Register<ZPackage>(RollRpc, Receive);
                peer.m_rpc.Register<string>(SettledRpc, Settled);
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned)), HarmonyPostfix]
        private static void RestoreReceipts(Player __instance)
        {
            if (__instance != Player.m_localPlayer || !ZNet.instance || ZNet.instance.IsServer()) return;
            awaitingSettlement.Clear();
            string prefix = ReceiptPrefix + ZNet.instance.GetWorldUID().ToString(CultureInfo.InvariantCulture) + ".";
            foreach (var pair in __instance.m_customData)
                if (pair.Key.StartsWith(prefix, StringComparison.Ordinal) && pair.Value != null) awaitingSettlement[pair.Value] = pair.Key;
            nextSettlement = 0f;
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown)), HarmonyPrefix]
        private static void Leaving()
        {
            awaitingSettlement.Clear();
            waiting = null;
        }

        private static void Settled(ZRpc rpc, string receipt)
        {
            if (ZNet.instance?.GetServerPeer()?.m_rpc != rpc || !awaitingSettlement.TryGetValue(receipt, out string key)) return;
            awaitingSettlement.Remove(receipt);
            var player = Player.m_localPlayer;
            if (player && player.m_customData.TryGetValue(key, out string saved) && saved == receipt) player.m_customData.Remove(key);
        }

        internal static void Tick()
        {
            if (awaitingSettlement.Count > 0 && Time.realtimeSinceStartup >= nextSettlement)
            {
                nextSettlement = Time.realtimeSinceStartup + 2f;
                var server = ZNet.instance?.GetServerPeer();
                var player = Player.m_localPlayer;
                if (player && server?.m_rpc.IsConnected() == true)
                    foreach (var pair in awaitingSettlement.Take(8).ToArray())
                    {
                        if (!CharacterCheckpoints.Available) PublishReceipt(player, pair.Value, pair.Key);
                        server.m_rpc.Invoke(AppliedRpc, pair.Key);
                    }
            }
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
            string nonce = package.ReadString();
            if (request == null || request.Nonce != nonce || request.Item != item || request.Quality != quality) return;
            waiting = null;
            var gui = InventoryGui.instance;
            var player = Player.m_localPlayer;
            if (!player) return;
            if (id.Length == 0) { player.Message(MessageHud.MessageType.Center, NoAnswer); return; }
            // The player may have left the forge or chosen something else meanwhile; the server keeps the roll for the next attempt.
            var upgrade = gui ? CraftUpgrade(gui) : null;
            if (!gui || !Window(gui).GetBool("visible") || !ServerDecides(gui) || CraftRecipe(gui).m_item.gameObject.name != item || upgrade.m_quality + 1 != quality ||
                !ReferenceEquals(upgrade, request.Selected) || player.GetCurrentCraftingStation() != request.Station || !player.GetInventory().ContainsItem(upgrade))
            {
                player.Message(MessageHud.MessageType.Center, Cancelled);
                return;
            }
            granted = roll;
            used = false;
            var profile = Game.instance.GetPlayerProfile();
            float before = profile.m_playerStats[0][PlayerStatType.Upgrades];
            try { DoCrafting(gui, player); }
            finally { granted = null; }
            float after = profile.m_playerStats[0][PlayerStatType.Upgrades];
            if (!used || after <= before || player.GetInventory().ContainsItem(upgrade)) return;
            // The receipt travels with the result, including a destroyed tool, in the same character save.
            string receipt = id + "|" + after.ToString("R", CultureInfo.InvariantCulture);
            string key = ReceiptKey(item, quality);
            player.m_customData[key] = receipt;
            profile.SavePlayerData(player);
            if (profile.Save())
            {
                awaitingSettlement[receipt] = key;
                if (!CharacterCheckpoints.Available) PublishReceipt(player, key, receipt);
                rpc.Invoke(AppliedRpc, receipt);
            }
        }

        private static void Decide(ZRpc rpc, ZPackage package)
        {
            string item = package.ReadString();
            int quality = package.ReadInt();
            string nonce = package.ReadString(), previous = package.ReadString();
            var peer = ZNet.instance.GetPeers().Find(candidate => candidate.m_rpc == rpc);
            var reply = new ZPackage();
            string id = "";
            float roll = 0f;
            float now = Time.realtimeSinceStartup;
            try
            {
                if (peer == null || !peer.IsReady() || !Guid.TryParseExact(nonce, "N", out _) || previous.Length > 80) return;
                string account = peer.m_socket.GetHostName();
                if (lastRequest.TryGetValue(account, out float last) && now - last < RequestInterval) throw new InvalidDataException("requests arrived too quickly");
                lastRequest[account] = now;
                if (item == null || item.Length > 80 || ObjectDB.instance.GetItemPrefab(item) == null || quality < 2 || quality > 1000)
                    throw new InvalidDataException("unknown item " + item + " or quality " + quality);
                string key = account + "|" + item + "|" + quality.ToString(CultureInfo.InvariantCulture);
                Load();
                if (unused.TryGetValue(key, out var pending))
                {
                    // A saved attempt settles even if its acknowledgement never reached us. A request without the
                    // client's receipt cannot conceal a result already present in the server's own character file.
                    bool settled = Settle(peer, key, pending);
                    if (!settled && BelongsToReceipt(pending, previous))
                        throw new InvalidDataException("waiting for the saved refinement");
                }
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
            reply.Write(nonce);
            rpc.Invoke(RollRpc, reply);
        }

        private static void PublishReceipt(Player player, string key, string receipt)
        {
            var data = GameFields.CharacterView(player).GetZDO();
            data.Set(key, receipt);
            ZDOMan.instance.ForceSendZDO(data.m_uid);
        }

        private static string ReceiptKey(string item, int quality) => ReceiptPrefix + ZNet.instance.GetWorldUID().ToString(CultureInfo.InvariantCulture)
            + "." + item + "." + quality.ToString(CultureInfo.InvariantCulture);

        private static bool BelongsToReceipt(Entry entry, string receipt) => receipt != null && receipt.StartsWith(entry.Id + "|", StringComparison.Ordinal);

        private static void Applied(ZRpc rpc, string receipt)
        {
            var peer = ZNet.instance.GetPeers().Find(candidate => candidate.m_rpc == rpc);
            if (peer == null || !peer.IsReady() || receipt == null || receipt.Length > 80) return;
            try
            {
                Load();
                var account = peer.m_socket.GetHostName();
                var match = unused.FirstOrDefault(pair => pair.Value.Account == account && BelongsToReceipt(pair.Value, receipt));
                if (match.Key == null || Settle(peer, match.Key, match.Value)) rpc.Invoke(SettledRpc, receipt);
            }
            catch (Exception error) { Debug.LogError("Progression Fix refinement settlement: " + error.GetBaseException().Message); }
        }

        // On a ServerCharacters world the receipt must have travelled with the result in a character save
        // already accepted by that server. On a world with local characters, Valheim entrusts that save to its player.
        private static bool Settle(ZNetPeer peer, string key, Entry entry)
        {
            string receipt;
            var profile = CharacterCheckpoints.Available ? CharacterCheckpoints.ReadSavedCharacter(peer) : null;
            if (CharacterCheckpoints.Available)
            {
                if (profile == null) return false;
                receipt = EnchantmentMigration.SavedCharacterValue(profile, ReceiptKey(entry.Item, entry.Quality));
            }
            else
            {
                var data = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (data == null || data.GetOwner() != peer.m_uid || data.GetPrefab() != "Player".GetStableHashCode()) return false;
                receipt = data.GetString(ReceiptKey(entry.Item, entry.Quality));
            }
            if (!BelongsToReceipt(entry, receipt) || !float.TryParse(receipt.Substring(entry.Id.Length + 1), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float count) || !(count > 0f) || float.IsInfinity(count) ||
                profile != null && profile.m_playerStats[0][PlayerStatType.Upgrades] < count) return false;
            Write(new Entry { Event = "applied", Id = entry.Id, Account = entry.Account, Character = peer.m_playerName,
                Item = entry.Item, Quality = entry.Quality, Roll = entry.Roll });
            unused.Remove(key);
            return true;
        }

        // Each world's rolls, read once: issued and reused ones stay unused until applied.
        private static void Load()
        {
            long world = ZNet.instance.GetWorldUID();
            if (journal != null && world == loadedWorld) return;
            var recovered = new Dictionary<string, Entry>();
            string path = Path.Combine(global::Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "refinement-rolls", world.ToString(CultureInfo.InvariantCulture) + ".jsonl");
            if (File.Exists(path))
                foreach (string line in WholeFile.CompleteLines(path))
                {
                    Entry entry;
                    try { entry = JsonConvert.DeserializeObject<Entry>(line); }
                    catch (JsonException error)
                    {
                        Debug.LogWarning("Progression Fix skipped a damaged line in " + path + ": " + error.Message);
                        continue;
                    }
                    if (entry == null) continue;
                    string key = entry.Account + "|" + entry.Item + "|" + entry.Quality.ToString(CultureInfo.InvariantCulture);
                    if (entry.Event == "applied") recovered.Remove(key);
                    else if (entry.Event == "issued" || entry.Event == "reused") recovered[key] = entry;
                    else throw new InvalidDataException("Unknown refinement journal event: " + entry.Event);
                }
            unused = recovered;
            journal = path;
            loadedWorld = world;
        }

        // Written before the roll is sent, so no roll reaches a player without a record.
        private static void Write(Entry entry)
        {
            entry.At = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Directory.CreateDirectory(Path.GetDirectoryName(journal));
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(entry) + "\n");
            try
            {
                using var stream = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            catch
            {
                // Replay before the next append if a short write left an incomplete tail in this process.
                journal = null;
                throw;
            }
        }
    }
}
