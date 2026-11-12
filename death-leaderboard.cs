using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class DeathLeaderboard
    {
        private sealed class DeliveryState
        {
            public DateTime RecordingSinceUtc;
            public DateTime CompletedUtc;
            public DateTime PendingUtc;
            public string PendingContent;
            public DateTime RetryAfterUtc;
        }

        private static ManualLogSource logger;
        private static FieldInfo database, configuration, deaths;
        private static PropertyInfo recordDate, recordName, recordPlayer;
        private static MethodInfo findAll, recordDeath, insertDeathRecord;
        private static ConstructorInfo createDeathRecord;
        private static FieldInfo databaseEngine;
        private static PropertyInfo collectionName, databaseMapper, documentField, bsonDate, bsonText;
        private static MethodInfo rawCollection, toDocument, rawInsert, rawFindAll;
        private static ConstructorInfo stringValue;
        private static object objectId;
        private static DeathAttribution.Source currentSource;
        private static HashSet<string> joinedPlayers;
        private static readonly Dictionary<ZRpc, ZDOID> recordedCharacters = new();
        private static readonly int deathMessage = "OnDeath".GetStableHashCode();
        private static TimeZoneInfo reportZone;
        private static int reportHour;
        private static DeathReport.Text wording;
        private static volatile bool running;
        private static DateTime nextCheckUtc;
        private static bool announced;
        private static bool killersAnnounced;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!Plugin.DailyDeathLeaderboard.Value) return;
            // DiscordConnector is a server-only optional dependency; clients do not acquire credentials.
            if (!PluginIds.TryGet(PluginIds.DiscordConnector, out var connector)) return;
            logger = log;
            var assembly = connector.Instance.GetType().Assembly;
            database = AccessTools.Field(connector.Instance.GetType(), "StaticDatabase");
            configuration = AccessTools.Field(connector.Instance.GetType(), "StaticConfig");
            deaths = AccessTools.Field(assembly.GetType("DiscordConnector.Records.Database"), "DeathCollection");
            var record = assembly.GetType("DiscordConnector.Records.SimpleStat", true);
            recordDate = AccessTools.Property(record, "Date");
            recordName = AccessTools.Property(record, "Name");
            recordPlayer = AccessTools.Property(record, "PlayerId");
            findAll = deaths == null ? null : AccessTools.Method(deaths.FieldType, "FindAll");
            insertDeathRecord = deaths == null ? null : AccessTools.Method(deaths.FieldType, "Insert", new[] { record });
            createDeathRecord = AccessTools.Constructor(record,
                new[] { typeof(string), typeof(string), typeof(float), typeof(float), typeof(float) });
            var handlers = assembly.GetType("DiscordConnector.Handlers", true);
            recordDeath = AccessTools.Method(handlers, "Death");
            joinedPlayers = (HashSet<string>)AccessTools.Field(handlers, "joinedPlayers")?.GetValue(null);
            Require("DiscordConnector death-record API", ("StaticDatabase", database), ("StaticConfig", configuration),
                ("Database.DeathCollection", deaths), ("DeathCollection.FindAll", findAll), ("SimpleStat.Date", recordDate),
                ("SimpleStat.Name", recordName), ("SimpleStat.PlayerId", recordPlayer), ("Handlers.Death", recordDeath),
                ("Handlers.joinedPlayers", joinedPlayers), ("DeathCollection.Insert", insertDeathRecord), ("SimpleStat constructor", createDeathRecord));
            var bsonDocument = assembly.GetType("LiteDB.BsonDocument", true);
            var bsonValue = assembly.GetType("LiteDB.BsonValue", true);
            var autoId = assembly.GetType("LiteDB.BsonAutoId", true);
            databaseEngine = AccessTools.Field(database.FieldType, "db");
            collectionName = AccessTools.Property(deaths.FieldType, "Name");
            databaseMapper = databaseEngine == null ? null : AccessTools.Property(databaseEngine.FieldType, "Mapper");
            rawCollection = databaseEngine?.FieldType.GetMethods().SingleOrDefault(method => method.Name == "GetCollection" &&
                !method.IsGenericMethod && method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(string));
            Require("DiscordConnector death database", ("Database.db", databaseEngine), ("DeathCollection.Name", collectionName),
                ("LiteDatabase.Mapper", databaseMapper), ("LiteDatabase.GetCollection(string, BsonAutoId)", rawCollection));
            toDocument = AccessTools.Method(databaseMapper.PropertyType, "ToDocument", new[] { typeof(Type), typeof(object) });
            documentField = bsonDocument.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SingleOrDefault(property => property.Name == "Item" && property.GetIndexParameters()[0].ParameterType == typeof(string));
            bsonDate = AccessTools.Property(bsonValue, "AsDateTime");
            bsonText = AccessTools.Property(bsonValue, "AsString");
            stringValue = AccessTools.Constructor(bsonValue, new[] { typeof(string) });
            rawInsert = AccessTools.Method(rawCollection.ReturnType, "Insert", new[] { bsonDocument });
            rawFindAll = AccessTools.Method(rawCollection.ReturnType, "FindAll");
            objectId = Enum.Parse(autoId, "ObjectId");
            Require("DiscordConnector raw death-record API", ("BsonMapper.ToDocument", toDocument), ("BsonDocument[string]", documentField),
                ("BsonValue.AsDateTime", bsonDate), ("BsonValue.AsString", bsonText), ("BsonValue(string)", stringValue),
                ("raw collection Insert(BsonDocument)", rawInsert), ("raw collection FindAll", rawFindAll));
            reportZone = DeathReport.ResolveZone(Plugin.DeathReportTimeZone.Value);
            reportHour = Plugin.DeathLeaderboardHour.Value;
            wording = new DeathReport.Text {
                PlayerTitle = Plugin.DeathReportPlayerTitle.Value, CauseTitle = Plugin.DeathReportCauseTitle.Value,
                OtherPlayers = Plugin.DeathReportOtherPlayers.Value, OtherCauses = Plugin.DeathReportOtherCauses.Value,
                NoDeaths = Plugin.DeathReportNoDeaths.Value, TotalDeaths = Plugin.DeathReportTotalDeaths.Value,
                MissingName = Plugin.DeathReportMissingName.Value, UnknownCause = Plugin.DeathReportUnknownCause.Value
            };

            // 3.2.0 records deaths from the same broadcast but can't read the cause, so its own prefix would add a second row
            // without one. The broadcast arrives even when the server has no local instance of the player's character.
            var connectorDeath = assembly.GetType("DiscordConnector.Patches.ZNetPatches+DeathRpc", true);
            harmony.Unpatch(AccessTools.Method(typeof(ZRoutedRpc), "HandleRoutedRPC"),
                AccessTools.DeclaredMethod(connectorDeath, "Prefix") ?? throw new MissingMethodException(connectorDeath.FullName, "Prefix"));
            harmony.Patch(AccessTools.Method(typeof(ZRoutedRpc), "RPC_RoutedRPC"),
                transpiler: new HarmonyMethod(typeof(DeathLeaderboard), nameof(ObserveDeathMessage)));
            harmony.Patch(AccessTools.Method(deaths.DeclaringType, "InsertSimpleStatRecord",
                new[] { deaths.FieldType, typeof(string), typeof(string), typeof(Vector3) }),
                prefix: new HarmonyMethod(typeof(DeathLeaderboard), nameof(StoreDeath)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "Disconnect"),
                postfix: new HarmonyMethod(typeof(DeathLeaderboard), nameof(ForgetPeer)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "Shutdown"),
                prefix: new HarmonyMethod(typeof(DeathLeaderboard), nameof(ClearDeaths)));
            logger.LogInfo($"Daily player and killer leaderboards initialized: {reportHour:00}:00 {reportZone.Id}, preceding 24 hours.");
        }

        private static void Require(string api, params (string Name, object Member)[] members)
        {
            foreach (var (name, member) in members)
                if (member == null) throw new InvalidOperationException(api + " changed: " + name + " is missing.");
        }

        private static IEnumerable<CodeInstruction> ObserveDeathMessage(IEnumerable<CodeInstruction> instructions,
            MethodBase __originalMethod)
        {
            var data = __originalMethod.GetMethodBody().LocalVariables.Single(local => local.LocalType == typeof(ZRoutedRpc.RoutedRPCData));
            var deserialize = AccessTools.Method(typeof(ZRoutedRpc.RoutedRPCData), "Deserialize");
            int changes = 0;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (!instruction.Calls(deserialize)) continue;
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldloc, data.LocalIndex);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DeathLeaderboard), nameof(ReceiveDeath)));
                changes++;
            }
            if (changes != 1) throw new InvalidOperationException("Native death-message routing changed.");
        }

        private static void ReceiveDeath(ZRpc rpc, ZRoutedRpc.RoutedRPCData data)
        {
            if (!ZNet.instance.IsServer() || data.m_methodHash != deathMessage || data.m_targetPeerID != ZRoutedRpc.Everybody) return;
            var peer = ZNet.instance.GetPeers().Find(candidate => candidate.m_rpc == rpc);
            if (peer == null || !peer.IsReady() || data.m_senderPeerID != peer.m_uid || peer.m_characterID == ZDOID.None ||
                data.m_targetZDO != peer.m_characterID ||
                (recordedCharacters.TryGetValue(rpc, out var previous) && previous == data.m_targetZDO) ||
                !joinedPlayers.Contains(peer.m_socket.GetHostName())) return;
            try
            {
                try { currentSource = DeathAttribution.Read(data.m_parameters); }
                catch (Exception error) { logger.LogWarning("Rejected death attribution: " + error.GetBaseException().Message); }
                recordDeath.Invoke(null, new object[] { peer });
                recordedCharacters[rpc] = data.m_targetZDO;
            }
            catch (Exception error) { logger.LogError("Could not record death: " + error.GetBaseException().Message); }
            finally { currentSource = null; }
        }

        private static bool StoreDeath(object collection, string playerName, string playerHostName, Vector3 pos)
        {
            if (!ReferenceEquals(collection, deaths.GetValue(database.GetValue(null)))) return true;
            var record = createDeathRecord.Invoke(new object[] { playerName, playerHostName, pos.x, pos.y, pos.z });
            if (currentSource == null) insertDeathRecord.Invoke(collection, new[] { record });
            else
            {
                var engine = databaseEngine.GetValue(database.GetValue(null));
                var document = toDocument.Invoke(databaseMapper.GetValue(engine), new[] { record.GetType(), record });
                documentField.SetValue(document, stringValue.Invoke(new object[] { currentSource.Key }), new object[] { "DeathSourceKey" });
                documentField.SetValue(document, stringValue.Invoke(new object[] { PlainName(currentSource.Name) }), new object[] { "DeathSourceName" });
                // One insert preserves the native record and its cause in the same database commit.
                rawInsert.Invoke(RawDeaths(), new[] { document });
            }
            return false;
        }

        private static object RawDeaths()
        {
            var storage = database.GetValue(null);
            return rawCollection.Invoke(databaseEngine.GetValue(storage), new[] {
                collectionName.GetValue(deaths.GetValue(storage)), objectId });
        }

        private static void ForgetPeer(ZNetPeer peer) { if (peer != null) recordedCharacters.Remove(peer.m_rpc); }
        private static void ClearDeaths() => recordedCharacters.Clear();

        internal static void Tick()
        {
            if (logger == null || running || ZNet.instance == null || !ZNet.instance.IsDedicated() ||
                DateTime.UtcNow < nextCheckUtc) return;
            nextCheckUtc = DateTime.UtcNow.AddMinutes(1);
            string directory = Path.Combine(Paths.BepInExRootPath, "death-leaderboard");
            string path = Path.Combine(directory, ZNet.instance.GetWorldUID().ToString(CultureInfo.InvariantCulture) +
                (Plugin.DeathLeaderboardDryRun.Value ? "-preview.dat" : ".dat"));
            bool preview = Plugin.DeathLeaderboardDryRun.Value;
            running = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    try { await Check(path, preview); }
                    catch (Exception error)
                    {
                        logger.LogError("Player death leaderboard failed: " + error.GetBaseException().Message);
                        nextCheckUtc = DateTime.UtcNow.AddMinutes(5);
                    }
                    string killerPath = Path.Combine(directory, Path.GetFileName(path).Replace(preview ? "-preview.dat" : ".dat", "") +
                        (preview ? "-killers-preview.dat" : "-killers.dat"));
                    await Check(killerPath, preview, true);
                }
                catch (Exception error)
                {
                    logger.LogError("Death leaderboard failed: " + error.GetBaseException().Message);
                    nextCheckUtc = DateTime.UtcNow.AddMinutes(5);
                }
                finally { running = false; }
            });
        }

        private static async Task Check(string path, bool preview, bool killers = false)
        {
            var config = configuration.GetValue(null);
            if (!(bool)Read(config, "CollectStatsEnabled") || !(bool)Read(config, "StatsDeathEnabled"))
                throw new InvalidOperationException("Enable DiscordConnector player statistics and death statistics before reporting.");
            object collection = killers ? RawDeaths() : deaths.GetValue(database.GetValue(null));
            if (collection == null) throw new InvalidOperationException("DiscordConnector death database is not initialized.");
            var now = DateTime.UtcNow;
            var state = File.Exists(path) ? JsonConvert.DeserializeObject<DeliveryState>(File.ReadAllText(path)) :
                new DeliveryState { RecordingSinceUtc = now };
            if (state == null || state.RecordingSinceUtc == default)
                throw new InvalidDataException("Invalid death leaderboard state; restore its backup before reporting.");
            if (!File.Exists(path)) Save(path, state);
            DateTime end = LatestDeadline(now);
            // Exercise the real read/format path once on startup in the credential-free test instance.
            if (!(killers ? killersAnnounced : announced))
            {
                // Older records have no cause; the new ranking starts with its own complete reporting window.
                string initialReport = killers ? ComposeKillers(collection, end) : Compose(collection, end);
                logger.LogInfo((killers ? "Killer" : "Player death") + " leaderboard database readable; first full report after " +
                    state.RecordingSinceUtc.AddHours(24).ToString("O") + ".");
                if (preview && initialReport != null) logger.LogInfo((killers ? "Killer" : "Player death") + " leaderboard preview (not delivered):\n" + initialReport);
                if (killers) killersAnnounced = true; else announced = true;
            }
            // The server stopped while a delivery was in flight, so Discord may already show that report.
            if (state.PendingUtc != default) SkipUncertain(path, state, "was interrupted by a server stop");
            if (end <= state.CompletedUtc || end.AddHours(-24) < state.RecordingSinceUtc || now < state.RetryAfterUtc) return;
            string content = killers ? ComposeKillers(collection, end) : Compose(collection, end);
            if (preview)
            {
                logger.LogInfo((killers ? "Killer" : "Player death") + " leaderboard preview (not delivered):\n" + content);
            }
            else
            {
                string webhook = (string)Read(Read(config, "PrimaryWebhook"), "Url");
                if (!Uri.TryCreate(webhook, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                    (uri.Host != "discord.com" && uri.Host != "discordapp.com") ||
                    !Regex.IsMatch(uri.AbsolutePath, @"^/api(?:/v[0-9]+)?/webhooks/[0-9]+/[A-Za-z0-9_-]+$"))
                    throw new InvalidOperationException("Configure a valid DiscordConnector primary Discord webhook before reporting.");
                var target = new UriBuilder(uri) { Query = "wait=true" };
                state.PendingUtc = end;
                state.PendingContent = content;
                Save(path, state);
                using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) })
                using (var body = new StringContent(JsonConvert.SerializeObject(new
                {
                    content, allowed_mentions = new { parse = new string[0] }
                }), Encoding.UTF8, "application/json"))
                {
                    // Retry only when Discord certainly did not post; otherwise skip this report rather than risk a duplicate.
                    // HTTP exceptions can contain the secret URL, so log only the error type.
                    HttpResponseMessage response;
                    try { response = await client.PostAsync(target.Uri, body); }
                    catch (Exception error)
                    {
                        if (NotSent(error)) Retry(path, state, now.AddMinutes(15), "Discord could not be reached (" + error.GetType().Name + ")");
                        else SkipUncertain(path, state, "got no answer from Discord (" + error.GetType().Name + ")");
                        return;
                    }
                    using (response)
                    {
                        int status = (int)response.StatusCode;
                        if (status == 429)
                        {
                            var wait = RetryAfterSeconds(response, await response.Content.ReadAsStringAsync());
                            Retry(path, state, now.AddSeconds(wait), "Discord rate limited the report");
                            return;
                        }
                        if (status >= 500)
                        {
                            SkipUncertain(path, state, "failed with HTTP " + status + " from Discord");
                            return;
                        }
                        if (!response.IsSuccessStatusCode)
                        {
                            Retry(path, state, now.AddMinutes(15), "Discord refused it with HTTP " + status + "; check the webhook");
                            return;
                        }
                    }
                }
            }
            state.CompletedUtc = end;
            state.PendingUtc = default;
            state.PendingContent = null;
            Save(path, state);
            logger.LogInfo((killers ? "Killer" : "Player death") + " leaderboard " + (preview ? "previewed" : "delivered") + " for " + end.ToString("O"));
        }

        private static void Retry(string path, DeliveryState state, DateTime retryAfterUtc, string reason)
        {
            state.PendingUtc = default;
            state.PendingContent = null;
            state.RetryAfterUtc = retryAfterUtc;
            Save(path, state);
            logger.LogWarning("Death leaderboard not delivered: " + reason + ". Retrying after " + retryAfterUtc.ToString("O") + ".");
        }

        // Discord may have posted this report. Mark it done so later reports continue, and keep its text in the log.
        private static void SkipUncertain(string path, DeliveryState state, string reason)
        {
            logger.LogWarning("Death leaderboard delivery for " + state.PendingUtc.ToString("O") + " " + reason +
                "; it will not be resent, to avoid a duplicate. If Discord does not show it, post this text manually:\n" +
                state.PendingContent);
            if (state.PendingUtc > state.CompletedUtc) state.CompletedUtc = state.PendingUtc;
            state.PendingUtc = default;
            state.PendingContent = null;
            Save(path, state);
        }

        // True only when the request provably never reached Discord: name lookup, connection or TLS setup failed.
        private static bool NotSent(Exception error)
        {
            for (var current = error; current != null; current = current.InnerException)
            {
                if (current is AuthenticationException) return true;
                if (current is WebException web && (web.Status == WebExceptionStatus.NameResolutionFailure ||
                    web.Status == WebExceptionStatus.ProxyNameResolutionFailure || web.Status == WebExceptionStatus.ConnectFailure ||
                    web.Status == WebExceptionStatus.SecureChannelFailure || web.Status == WebExceptionStatus.TrustFailure))
                    return true;
                if (current is SocketException socket && (socket.SocketErrorCode == SocketError.HostNotFound ||
                    socket.SocketErrorCode == SocketError.TryAgain || socket.SocketErrorCode == SocketError.NoData ||
                    socket.SocketErrorCode == SocketError.ConnectionRefused || socket.SocketErrorCode == SocketError.NetworkDown ||
                    socket.SocketErrorCode == SocketError.NetworkUnreachable || socket.SocketErrorCode == SocketError.HostUnreachable))
                    return true;
            }
            return false;
        }

        private static double RetryAfterSeconds(HttpResponseMessage response, string body)
        {
            double seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0;
            // Discord answers with JSON, but a proxy in front of it can answer with HTML. Keep the default wait then.
            try
            {
                var value = JObject.Parse(body)["retry_after"];
                if (value != null && (value.Type == JTokenType.Float || value.Type == JTokenType.Integer))
                    seconds = Math.Max(seconds, (double)value);
            }
            catch (JsonReaderException) { }
            return Math.Max(300, seconds);
        }

        internal static DateTime LatestDeadline(DateTime utcNow) => DeathReport.LatestDeadline(utcNow, reportZone, reportHour);

        private static string Compose(object collection, DateTime end)
        {
            DateTime start = end.AddHours(-24);
            var totals = new Dictionary<string, DeathReport.Row>(StringComparer.Ordinal);
            // The connector's date queries cover whole calendar days; this window runs from report hour to report hour.
            foreach (var record in (IEnumerable)findAll.Invoke(collection, null))
            {
                DateTime date = ((DateTime)recordDate.GetValue(record)).ToUniversalTime();
                if (date < start || date >= end) continue;
                string player = (string)recordPlayer.GetValue(record);
                if (string.IsNullOrEmpty(player)) throw new InvalidDataException("Death record has no player identity.");
                Count(totals, player, (string)recordName.GetValue(record), date);
            }
            return DeathReport.Format(totals.Values, false, wording);
        }

        private static string ComposeKillers(object collection, DateTime end)
        {
            DateTime start = end.AddHours(-24);
            var totals = new Dictionary<string, DeathReport.Row>(StringComparer.Ordinal);
            foreach (var record in (IEnumerable)rawFindAll.Invoke(collection, null))
            {
                DateTime date = ((DateTime)bsonDate.GetValue(documentField.GetValue(record, new object[] { "Date" }))).ToUniversalTime();
                if (date < start || date >= end) continue;
                string key = (string)bsonText.GetValue(documentField.GetValue(record, new object[] { "DeathSourceKey" }));
                string name = (string)bsonText.GetValue(documentField.GetValue(record, new object[] { "DeathSourceName" }));
                // A death recorded without a readable cause, such as one from a mismatched client, still counts.
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name)) { key = "unknown"; name = "Unknown"; }
                Count(totals, key, name, date);
            }
            return DeathReport.Format(totals.Values, true, wording);
        }

        private static void Count(Dictionary<string, DeathReport.Row> totals, string key, string name, DateTime date)
        {
            if (!totals.TryGetValue(key, out var row)) totals[key] = row = new DeathReport.Row { Key = key };
            row.Count++;
            if (date >= row.LatestUtc) { row.LatestUtc = date; row.Name = name; }
        }

        private static string PlainName(string name) => DeathReport.Name(name, "Player");

        private static object Read(object instance, string name) =>
            AccessTools.Property(instance.GetType(), name)?.GetValue(instance) ??
            throw new MissingMemberException("DiscordConnector setting unavailable: " + name);

        private static void Save(string path, DeliveryState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WholeFile.Replace(path, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(state, Formatting.Indented)));
        }
    }
}
