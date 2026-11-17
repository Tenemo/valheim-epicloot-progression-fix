using System;
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
    [HarmonyPatch]
    internal static class CoinIncomeChannel
    {
        private const string EventsRpc = "ProgressionFix.CoinIncome.Events.1", AckRpc = "ProgressionFix.CoinIncome.Ack.1";
        private static ManualLogSource logger;
        private static ZNet network;
        private static CoinIncomeOutbox outbox;
        private static CoinIncomeJournal journal;
        private static string outboxPath;
        private static long character, world;
        private static float nextSend, nextError;
        internal static bool Flushing;
        internal static bool Drained => !Active || outbox.Pending.Count == 0;
        private static readonly Dictionary<ZRpc, float> lastBatch = new Dictionary<ZRpc, float>();
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings { MaxDepth = 8 };
        internal static bool Enabled => Plugin.CoinIncomeEnabled.Value && CharacterCheckpoints.Available;
        internal static bool Active => Enabled && outbox != null && Player.m_localPlayer != null &&
            network == ZNet.instance && character == Player.m_localPlayer.GetPlayerID() && world == network.GetWorldUID();
        internal static string JournalPath => Path.Combine(CharacterCheckpoints.PrivateSaveDirectory(), "coin-income",
            ZNet.instance.GetWorldUID().ToString(CultureInfo.InvariantCulture) + ".jsonl");

        internal static void Initialize(ManualLogSource log) => logger = log;

        [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPostfix]
        private static void Register(ZNet __instance, ZNetPeer peer)
        {
            if (!CharacterCheckpoints.Available) return;
            if (__instance.IsServer())
                peer.m_rpc.Register<string>(EventsRpc, (rpc, payload) => Receive(rpc, payload));
            else if (peer.m_server)
                peer.m_rpc.Register<string>(AckRpc, (rpc, payload) => {
                    if (!Active || ZNet.instance.GetServerPeer()?.m_rpc != rpc) return;
                    try
                    {
                        if (payload == null || payload.Length > 4096) throw new InvalidDataException("Invalid currency acknowledgement.");
                        outbox.Acknowledge(JsonConvert.DeserializeObject<List<string>>(payload, Json));
                    }
                    catch (Exception error) { Error(error); }
                });
        }

        internal static void Tick()
        {
            if (!Enabled || ZNet.instance == null) return;
            if (network != ZNet.instance)
            {
                network = ZNet.instance;
                journal = null; outbox = null; outboxPath = null; character = world = 0;
                lastBatch.Clear();
            }
            if (Player.m_localPlayer == null || network.GetServerPeer()?.m_rpc.IsConnected() != true) return;
            try
            {
                var player = Player.m_localPlayer;
                long playerId = player.GetPlayerID();
                if (playerId == 0) return;
                // The outbox's path names only its world and character.
                string path = outboxPath == null || playerId != character || network.GetWorldUID() != world ? Path.Combine(Paths.BepInExRootPath, "coin-income-state",
                    network.GetWorldUID().ToString(CultureInfo.InvariantCulture) + "-" + playerId.ToString(CultureInfo.InvariantCulture) + ".dat") : outboxPath;
                if (path != outboxPath)
                {
                    bool first = !File.Exists(path);
                    outbox = new CoinIncomeOutbox(path);
                    outboxPath = path; character = playerId; world = network.GetWorldUID();
                    foreach (var item in player.GetInventory().GetAllItems())
                        if (CoinIncome.IsCurrency(item)) CoinIncome.SetPending(item, 0);
                    if (first)
                        foreach (string currency in new[] { "Coins", "GoldBountyToken" })
                        {
                            int amount = CoinIncome.Count(player.GetInventory(), currency);
                            if (amount > 0) Record(currency, amount, "opening", "opening balance", null);
                        }
                    outbox.Save();
                }
                if (Time.realtimeSinceStartup < nextSend) return;
                nextSend = Time.realtimeSinceStartup + (Flushing ? 0.2f : 2f);
                outbox.Save();
                if (outbox.Pending.Count > 0)
                    network.GetServerPeer().m_rpc.Invoke(EventsRpc, JsonConvert.SerializeObject(outbox.Pending.Take(24).ToList()));
            }
            catch (Exception error) { Error(error); }
        }

        internal static void Record(string currency, int amount, string kind, string source, string reference)
        {
            if (!Active || amount <= 0) return;
            var item = new CoinIncomeEvent { Id = Guid.NewGuid().ToString("N"), At = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                World = world, Character = character, Currency = currency, Amount = amount, Kind = kind, Source = source, Reference = reference };
            try { outbox.Add(item); }
            catch (Exception error) { Error(error); } // Tick saves it before sending.
        }

        private static void Receive(ZRpc rpc, string payload)
        {
            if (!Enabled || ZNet.instance == null || !ZNet.instance.IsDedicated()) return;
            try
            {
                if (payload == null || payload.Length > 32768) throw new InvalidDataException("Invalid currency batch size.");
                if (lastBatch.TryGetValue(rpc, out float previous) && Time.realtimeSinceStartup - previous < 0.1f)
                    throw new InvalidDataException("Currency batches arrived too quickly.");
                lastBatch[rpc] = Time.realtimeSinceStartup;
                if (!CharacterCheckpoints.PrivatePeerIdentity(rpc, out string account, out string player, out long playerId))
                    throw new InvalidDataException("Currency sender has no authenticated character.");
                var items = JsonConvert.DeserializeObject<List<CoinIncomeEvent>>(payload, Json);
                if (items == null || items.Count == 0 || items.Count > 24) throw new InvalidDataException("Invalid currency batch.");
                foreach (var item in items)
                {
                    CoinIncomeRules.ValidateIdentity(item, ZNet.instance.GetWorldUID(), playerId);
                }
                if (journal == null) journal = new CoinIncomeJournal(JournalPath);
                journal.Append(account, player, items);
                rpc.Invoke(AckRpc, JsonConvert.SerializeObject(items.Select(item => item.Id).ToList()));
            }
            catch (Exception error) { Error(error); }
        }

        [HarmonyPatch(typeof(Game), "Shutdown"), HarmonyPrefix]
        private static void SaveOnLogout()
        {
            try { outbox?.Save(); }
            catch (Exception error) { Error(error); }
        }

        internal static void Error(Exception error)
        {
            if (Time.realtimeSinceStartup < nextError) return;
            nextError = Time.realtimeSinceStartup + 10f;
            logger?.LogError("Private coin income: " + error.GetBaseException().Message);
        }
    }
}
