using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Delivers the client's item activity events to the dedicated server in batches every 2 s. The server appends them to
    // saves/item-activity/<world>/<UTC date>.jsonl on a background thread and acknowledges each batch only after it is on
    // disk; the client keeps unacknowledged events in BepInEx/item-activity-state and sends them again after a reconnect.
    // Treasure discoveries are written to their own file there at once and go ahead of the other events, every 5 s until
    // acknowledged; the server records when it began accepting them in the world's treasure-recording.json.
    [HarmonyPatch]
    internal static class ItemActivityChannel
    {
        private const string EventsRpc = "ProgressionFix.ItemActivity.Events.1", AckRpc = "ProgressionFix.ItemActivity.Ack.1";
        private const int BatchSize = 32, Capacity = 5000;
        private static ManualLogSource logger;
        private static ZNet network;
        private static ItemActivityOutbox outbox;
        private static ItemActivityJournal journal;
        private static ItemActivityWriter writer;
        private static TreasureReceipts treasures;
        private static TreasureRecordingStart recordingStart;
        private static string outboxPath, treasuresPath;
        private static long character, world;
        private static float nextSend, nextSave, nextError, nextTreasureSend;
        private static readonly HashSet<string> overdue = new HashSet<string>();
        private static readonly Dictionary<ZRpc, ItemActivityBatchLimit> batchLimits = new Dictionary<ZRpc, ItemActivityBatchLimit>();
        private static readonly ConcurrentQueue<(ZRpc Rpc, List<string> Ids, string Treasures)> written = new ConcurrentQueue<(ZRpc, List<string>, string)>();
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings { MaxDepth = 8 };
        internal static bool Enabled => Plugin.ItemActivityEnabled.Value && CharacterCheckpoints.Available;
        internal static bool Active => Enabled && outbox != null && Player.m_localPlayer != null &&
            network == ZNet.instance && character == Player.m_localPlayer.GetPlayerID() && world == network.GetWorldUID();
        internal static string JournalDirectory => Path.Combine(CharacterCheckpoints.PrivateSaveDirectory(), "item-activity",
            ZNet.instance.GetWorldUID().ToString(CultureInfo.InvariantCulture));

        internal static void Initialize(ManualLogSource log) => logger = log;
        private static ItemActivityWriter Writer => writer ?? (writer = new ItemActivityWriter(256));

        [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPostfix]
        private static void Register(ZNet __instance, ZNetPeer peer)
        {
            if (!CharacterCheckpoints.Available) return;
            if (__instance.IsServer())
                peer.m_rpc.Register<string>(EventsRpc, (rpc, payload) => Receive(rpc, payload));
            else if (peer.m_server)
                peer.m_rpc.Register<string>(AckRpc, (rpc, payload) => {
                    if (!Enabled || ZNet.instance.GetServerPeer()?.m_rpc != rpc) return;
                    try
                    {
                        if (payload == null || payload.Length > 4096) throw new InvalidDataException("Invalid item activity acknowledgement.");
                        var ids = JsonConvert.DeserializeObject<List<string>>(payload, Json);
                        if (Active) outbox.Acknowledge(ids);
                        if (treasures != null && treasures.Acknowledge(ids) > 0) overdue.ExceptWith(ids);
                    }
                    catch (Exception error) { Error(error); }
                });
        }

        // The store of the current world and character, opened when either changes; receipts left unsaved are reported.
        private static TreasureReceipts Treasures(long worldId, long playerId)
        {
            string path = Path.Combine(Paths.BepInExRootPath, "item-activity-state",
                worldId.ToString(CultureInfo.InvariantCulture) + "-" + playerId.ToString(CultureInfo.InvariantCulture) + ".treasure.json");
            if (path == treasuresPath && treasures != null) return treasures;
            if (treasures != null && treasures.Unsaved)
                logger?.LogError($"Private item activity: {treasures.Pending.Count} treasure discovery receipt(s) could not be saved before the character or world changed.");
            treasures = null;
            treasuresPath = path;
            treasures = new TreasureReceipts(path);
            if (treasures.Unreadable != null) logger?.LogError("Private item activity: unreadable treasure discovery receipts were kept aside as " + treasures.Unreadable);
            return treasures;
        }

        // Called while EpicLoot marks the map found, before its next save can remove the map.
        internal static void RecordTreasure(TreasureDiscovery treasure, Vector3 at, string source, string detail)
        {
            var player = Player.m_localPlayer;
            if (!Enabled || player == null || ZNet.instance == null || ZNet.instance.IsDedicated())
            {
                logger?.LogError($"Private item activity: a {treasure.Biome} treasure find was not recorded because recording is unavailable.");
                return;
            }
            var entry = new ItemActivityEvent { Id = Guid.NewGuid().ToString("N"), At = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                World = ZNet.instance.GetWorldUID(), Character = player.GetPlayerID(), Action = ItemActivityRules.Treasure, Count = 1,
                Position = $"{at.x:0},{at.y:0},{at.z:0}", Biome = ItemActivityRules.Clip(Heightmap.FindBiome(at).ToString(), 20),
                Source = ItemActivityRules.Clip(source, 120), Detail = ItemActivityRules.Clip(detail, 400), Treasure = treasure };
            var store = Treasures(entry.World, entry.Character);
            try
            {
                bool added = store.Add(entry);
                logger?.LogInfo($"Treasure discovery {(added ? "recorded" : "already waiting for the server")}: {treasure.Biome} {treasure.Interval + 1}" +
                    (treasure.Owner == 0 ? ", without an owner: " + detail : "."));
            }
            catch (Exception error) { logger?.LogError("Private item activity: a treasure discovery receipt could not be saved and stays pending in memory: " + error.GetBaseException().Message); }
        }

        internal static void Tick()
        {
            if (!Enabled || ZNet.instance == null) return;
            while (writer != null && writer.TakeError(out var failure)) Error(failure);
            if (network != ZNet.instance)
            {
                network = ZNet.instance;
                journal = null; outbox = null; outboxPath = null; character = world = 0; recordingStart = null;
                batchLimits.Clear();
            }
            if (network.IsDedicated())
            {
                if (recordingStart == null && network.GetWorldUID() != 0)
                    try
                    {
                        recordingStart = TreasureRecordingStart.Record(JournalDirectory, network.GetWorldUID(), network.GetWorldName(), Plugin.Version, DateTime.UtcNow);
                        logger?.LogInfo($"Treasure discovery recording for this world began {recordingStart.Activated} with Progression Fix {recordingStart.Recorder}.");
                    }
                    catch (Exception error) { Error(error); }
                while (written.TryDequeue(out var batch))
                {
                    if (batch.Treasures != null) logger?.LogInfo("Recorded treasure discovery receipt(s): " + batch.Treasures);
                    if (batch.Rpc.IsConnected()) batch.Rpc.Invoke(AckRpc, JsonConvert.SerializeObject(batch.Ids));
                }
                return;
            }
            if (Player.m_localPlayer == null || network.GetServerPeer()?.m_rpc.IsConnected() != true) return;
            try
            {
                var player = Player.m_localPlayer;
                long playerId = player.GetPlayerID();
                if (playerId == 0) return;
                // Before the item backlog, which a damaged file can stop.
                float now = Time.realtimeSinceStartup;
                var store = Treasures(network.GetWorldUID(), playerId);
                if (store.Unsaved)
                    try { store.Save(); logger?.LogInfo("Pending treasure discovery receipts are saved again."); }
                    catch (Exception error) { Error(error); }
                if (store.Pending.Count > 0 && now >= nextTreasureSend)
                {
                    nextTreasureSend = now + 5f;
                    network.GetServerPeer().m_rpc.Invoke(EventsRpc, JsonConvert.SerializeObject(store.Pending.Take(BatchSize).ToList()));
                    foreach (var waiting in store.Pending)
                        if (DateTime.UtcNow - DateTime.Parse(waiting.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) > TimeSpan.FromMinutes(1) && overdue.Add(waiting.Id))
                            logger?.LogError($"Private item activity: the server has not accepted the {waiting.Treasure.Biome} treasure discovery receipt {waiting.Id} for a minute; it stays pending.");
                }
                // The outbox's path names only its world and character.
                string path = outboxPath == null || playerId != character || network.GetWorldUID() != world ? Path.Combine(Paths.BepInExRootPath, "item-activity-state",
                    network.GetWorldUID().ToString(CultureInfo.InvariantCulture) + "-" + playerId.ToString(CultureInfo.InvariantCulture) + ".dat") : outboxPath;
                if (path != outboxPath)
                {
                    outbox = new ItemActivityOutbox(path, Capacity);
                    outboxPath = path; character = playerId; world = network.GetWorldUID();
                    Record("session-start", null, 0, 0, null, false, null, null, null);
                    ItemActivity.BeginSession(player);
                }
                if (outbox.Lost > 0 && outbox.Pending.Count < Capacity)
                {
                    int lost = outbox.TakeLost();
                    Record("gap", null, lost, 0, null, false, null, null, $"{lost} events were not recorded while the unsent backlog was full");
                }
                if (outbox.Changed && now >= nextSave)
                {
                    var copy = outbox.Copy();
                    string target = outboxPath;
                    if (!Writer.Post(() => ItemActivityOutbox.Save(target, copy))) Error(new IOException("The item activity writer is busy."));
                    nextSave = now + 2f;
                }
                if (now < nextSend || outbox.Pending.Count == 0) return;
                nextSend = now + 2f;
                string payload = JsonConvert.SerializeObject(outbox.Pending.Take(BatchSize).ToList());
                network.GetServerPeer().m_rpc.Invoke(EventsRpc, payload);
            }
            catch (Exception error) { Error(error); }
        }

        internal static void Record(string action, string item, int count, int quality, string tracking, bool begins, string source, string origin, string detail, Vector3? at = null)
        {
            if (!Active && action != "session-start") return;
            var position = at ?? Player.m_localPlayer.transform.position;
            var entry = new ItemActivityEvent { Id = Guid.NewGuid().ToString("N"), At = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                World = world, Character = character, Action = action, Item = ItemActivityRules.Clip(item, 80), Count = count, Quality = quality,
                Tracking = tracking, Begins = begins, Position = $"{position.x:0},{position.y:0},{position.z:0}",
                Biome = ItemActivityRules.Clip(Heightmap.FindBiome(position).ToString(), 20), Source = ItemActivityRules.Clip(source, 120),
                Origin = ItemActivityRules.Clip(origin, 160), Detail = ItemActivityRules.Clip(detail, 400) };
            try { outbox.Add(entry); }
            catch (Exception error) { Error(error); }
        }

        private static void Receive(ZRpc rpc, string payload)
        {
            if (!Enabled || ZNet.instance == null || !ZNet.instance.IsDedicated()) return;
            try
            {
                if (payload == null || payload.Length > 65536) throw new InvalidDataException("Invalid item activity batch size.");
                if (!batchLimits.TryGetValue(rpc, out var limit)) batchLimits.Add(rpc, limit = new ItemActivityBatchLimit());
                if (!limit.TryAccept(Time.realtimeSinceStartup))
                    throw new InvalidDataException("Item activity batches arrived too quickly.");
                if (!CharacterCheckpoints.PrivatePeerIdentity(rpc, out string account, out string player, out long playerId))
                    throw new InvalidDataException("Item activity sender has no authenticated character.");
                var items = JsonConvert.DeserializeObject<List<ItemActivityEvent>>(payload, Json);
                if (items == null || items.Count == 0 || items.Count > BatchSize) throw new InvalidDataException("Invalid item activity batch.");
                foreach (var item in items) ItemActivityRules.ValidateIdentity(item, ZNet.instance.GetWorldUID(), playerId);
                var found = items.Where(item => item.Action == ItemActivityRules.Treasure).ToList();
                if (found.Count > 0 && recordingStart == null)
                    throw new InvalidDataException("Treasure discoveries arrived before this world's recording start was saved.");
                string treasures = found.Count == 0 ? null : player + ": " + string.Join(", ", found.Select(item =>
                    $"{item.Treasure.Biome} {item.Treasure.Interval + 1}{(item.Treasure.Owner == 0 ? " without an owner" : "")} ({item.Id})"));
                if (journal == null) journal = new ItemActivityJournal(JournalDirectory);
                var target = journal;
                var ids = items.Select(item => item.Id).ToList();
                // A refused batch is not acknowledged, so the client sends it again.
                if (!Writer.Post(() => { int appended = target.Append(account, player, items, DateTime.UtcNow); written.Enqueue((rpc, ids, appended > 0 ? treasures : null)); }))
                    throw new IOException("The item activity writer is busy.");
            }
            catch (Exception error) { Error(error); }
        }

        [HarmonyPatch(typeof(Game), "Shutdown"), HarmonyPrefix]
        private static void SaveOnLogout()
        {
            try
            {
                if (!Active) return;
                Record("session-end", null, 0, 0, null, false, null, null, null);
                // After any save already queued, so the last one written is complete.
                var copy = outbox.Copy();
                string target = outboxPath;
                using (var done = new System.Threading.ManualResetEventSlim())
                {
                    if (Writer.Post(() => { try { ItemActivityOutbox.Save(target, copy); } finally { done.Set(); } })) done.Wait(2000);
                    else ItemActivityOutbox.Save(target, copy);
                }
            }
            catch (Exception error) { Error(error); }
        }

        internal static void Error(Exception error)
        {
            if (Time.realtimeSinceStartup < nextError) return;
            nextError = Time.realtimeSinceStartup + 10f;
            logger?.LogError("Private item activity: " + error.GetBaseException().Message);
        }
    }
}
