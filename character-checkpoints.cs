using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A world save does not prove that a client's character reached disk. Each checkpoint carries a request ID,
    // uses the game's complete character serializer, and is checked against the file ServerCharacters wrote.
    internal static class CharacterCheckpoints
    {
        private const string RequestRpc = Plugin.Id + ".checkpoint-request";
        private const string ReplyRpc = Plugin.Id + ".checkpoint-profile";
        private const string ErrorRpc = Plugin.Id + ".checkpoint-error";
        private const float RequestTimeout = 60f;
        private const int MaximumProfileBytes = 16 * 1024 * 1024;
        private static readonly Dictionary<ZRpc, Pending> pending = new();
        private static Func<ZNetPeer, string, byte[], IEnumerable<bool>> send;
        private static Func<Action<ZRpc, byte[]>, Action<ZRpc, ZPackage>> receive;
        private static Func<ZRpc, byte[], PlayerProfile> saveProfile;
        private static Func<PlayerProfile, byte[], bool> loadProfile;
        private static Func<string, string> accountId;
        private static Func<string, bool> forbiddenName;
        private static Func<string> characterDirectory;
        private static AccessTools.FieldRef<bool> nativeSaving, nativeServerCharacter, nativeEmergency;
        private static IDictionary inventories, peerNames;
        private static Action<Game, bool> shutdownGame;
        private static MonoBehaviour runner;
        private static ManualLogSource logger;
        private static ZNet network;
        private static string controlDirectory, phase = "idle", error = "";
        private static Request operation;
        private static float nextPoll, nextStatus, nextCheckpoint;
        private static uint savedWorld;
        private static bool running, quitting, acceptingCheckpoint, clientBusy;
        private static Capture capture;
        private static readonly int ProcessId = Process.GetCurrentProcess().Id;
        private static readonly ConditionalWeakTable<ZRpc, Identity> Identities = new ConditionalWeakTable<ZRpc, Identity>();

        private sealed class Identity
        {
            internal string Account, Name;
            internal long PlayerId;
        }

        private sealed class Request
        {
            public string id;
            public int pid;
            public string action;
        }

        private sealed class Pending
        {
            internal ZNetPeer Peer;
            internal string Name, Account, Path;
            internal long PlayerId;
            internal float Deadline;
            internal byte[] Saved;
            internal string Error;
        }

        private sealed class Capture
        {
            internal ZRpc Rpc;
            internal string Id;
            internal bool Logout;
            internal byte[] Bytes;
            internal string Error;
        }

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log, MonoBehaviour host)
        {
            if (!PluginIds.TryGet(PluginIds.ServerCharacters, out var plugin)) return;
            logger = log;
            runner = host;
            var assembly = plugin.Instance.GetType().Assembly;
            var shared = assembly.GetType("ServerCharacters.Shared", true);
            var client = assembly.GetType("ServerCharacters.ClientSide", true);
            var server = assembly.GetType("ServerCharacters.ServerSide", true);
            var receiver = assembly.GetType("ServerCharacters.ServerSide+PatchZNetOnNewConnection", true);
            var utils = assembly.GetType("ServerCharacters.Utils", true);
            send = AccessTools.MethodDelegate<Func<ZNetPeer, string, byte[], IEnumerable<bool>>>(AccessTools.Method(shared, "sendCompressedDataToPeer"));
            receive = AccessTools.MethodDelegate<Func<Action<ZRpc, byte[]>, Action<ZRpc, ZPackage>>>(AccessTools.Method(shared, "receiveCompressedFromPeer"));
            saveProfile = AccessTools.MethodDelegate<Func<ZRpc, byte[], PlayerProfile>>(AccessTools.Method(receiver, "onReceivedProfile"));
            var load = AccessTools.Method(shared, "LoadPlayerProfileFromBytes");
            if (load == null || load.ReturnType != typeof(bool) || !load.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(PlayerProfile), typeof(byte[]) }))
                throw new MissingMethodException("ServerCharacters profile loader changed.");
            harmony.Patch(load, transpiler: new HarmonyMethod(typeof(CharacterCheckpoints), nameof(PreserveCreationTime)) { priority = Priority.Last });
            IntegrationChanges.Defer(() => loadProfile = AccessTools.MethodDelegate<Func<PlayerProfile, byte[], bool>>(load), () => loadProfile = null);
            accountId = AccessTools.MethodDelegate<Func<string, string>>(AccessTools.Method(utils, "GetPlayerID"));
            forbiddenName = AccessTools.MethodDelegate<Func<string, bool>>(AccessTools.Method(shared, "CharacterNameIsForbidden"));
            characterDirectory = AccessTools.MethodDelegate<Func<string>>(AccessTools.PropertyGetter(utils, "CharacterSavePath"));
            nativeSaving = AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(client, "currentlySaving"));
            nativeServerCharacter = AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(client, "serverCharacter"));
            nativeEmergency = AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(client, "doEmergencyBackup"));
            inventories = (IDictionary)(AccessTools.Field(server, "Inventories") ?? throw new MissingFieldException(server.FullName, "Inventories")).GetValue(null);
            peerNames = (IDictionary)(AccessTools.Field(server, "peerProfileNameMap") ??
                throw new MissingFieldException(server.FullName, "peerProfileNameMap")).GetValue(null);
            shutdownGame = AccessTools.MethodDelegate<Action<Game, bool>>(AccessTools.Method(typeof(Game), "Shutdown"));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"),
                postfix: new HarmonyMethod(typeof(CharacterCheckpoints), nameof(Register)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "RPC_ServerHandshake"),
                prefix: new HarmonyMethod(typeof(CharacterCheckpoints), nameof(AllowJoin)));
            var serializer = assembly.GetType("ServerCharacters.ClientSide+PatchPlayerProfileSave_Client", true);
            harmony.Patch(AccessTools.Method(serializer, "SaveCharacterToServer"),
                prefix: new HarmonyMethod(typeof(CharacterCheckpoints), nameof(CaptureProfile)));
            harmony.Patch(AccessTools.Method(receiver, "onReceivedProfile"),
                prefix: new HarmonyMethod(typeof(CharacterCheckpoints), nameof(AllowOrdinarySave)));
            harmony.Patch(AccessTools.Method(receiver, "onReceivedInventory"),
                prefix: new HarmonyMethod(typeof(CharacterCheckpoints), nameof(AllowOrdinarySave)));
            logger.LogInfo("Character checkpoints and verified shutdown enabled for ServerCharacters " + plugin.Metadata.Version + ".");
        }

        // The copied game loader drops the time and timezone. Saving that date on a UTC+2 server moves it back
        // a day on every round trip. Preserve the original instant so a server checkpoint retains every byte.
        private static IEnumerable<CodeInstruction> PreserveCreationTime(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(AccessTools.PropertyGetter(typeof(DateTimeOffset), nameof(DateTimeOffset.Date))),
                AccessTools.PropertyGetter(typeof(DateTimeOffset), nameof(DateTimeOffset.UtcDateTime)), 1,
                "The ServerCharacters profile creation-time reader changed.");

        private static bool Stopping => running && operation.action == "stop";

        private static void Register(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer())
            {
                var handler = receive(ReceiveProfile);
                peer.m_rpc.Register<ZPackage>(ReplyRpc, (rpc, package) => {
                    if (!running || !pending.TryGetValue(rpc, out var item) || item.Saved != null) return;
                    try { handler(rpc, package); }
                    catch (Exception exception) { item.Error = exception.GetBaseException().Message; }
                });
                peer.m_rpc.Register<string, string>(ErrorRpc, (rpc, id, message) => {
                    if (running && id == operation.id && pending.TryGetValue(rpc, out var item)) item.Error = message;
                });
            }
            else if (peer.m_server)
                peer.m_rpc.Register<string, bool>(RequestRpc, (rpc, id, logout) => {
                    if (ZNet.instance?.GetServerPeer()?.m_rpc == rpc)
                        runner.StartCoroutine(ClientCheckpoint(rpc, id, logout));
                });
        }

        private static bool AllowJoin(ZNet __instance, ZRpc rpc)
        {
            if (!__instance.IsServer() || !Stopping) return true;
            var peer = __instance.GetPeers().Find(candidate => candidate.m_rpc == rpc);
            // A failed connection, not the version mismatch the old code 3 told the joining player.
            rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorConnectFailed);
            if (peer != null) __instance.Disconnect(peer);
            return false;
        }

        // Once the final full profile arrived, older inventory packets and logout cleanup must not overwrite it.
        private static bool AllowOrdinarySave(ZRpc __0) => acceptingCheckpoint || !Stopping ||
            !pending.TryGetValue(__0, out var item) || item.Saved == null;

        private static IEnumerator ClientCheckpoint(ZRpc rpc, string id, bool logout)
        {
            float deadline = Time.realtimeSinceStartup + RequestTimeout - 5f;
            while (clientBusy || nativeSaving() || Player.m_localPlayer == null || Game.instance.WaitingForRespawn())
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    rpc.Invoke(ErrorRpc, id, "Character is not ready to save.");
                    yield break;
                }
                yield return null;
            }
            if (!nativeServerCharacter() || nativeEmergency())
            {
                rpc.Invoke(ErrorRpc, id, "Character is not in a normal ServerCharacters session.");
                yield break;
            }
            CoinIncomeChannel.Flushing = true;
            try
            {
                while (!CoinIncomeChannel.Drained)
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        rpc.Invoke(ErrorRpc, id, "Private currency receipts are not yet saved on the server.");
                        yield break;
                    }
                    CoinIncomeChannel.Tick();
                    yield return null;
                }
            }
            finally { CoinIncomeChannel.Flushing = false; }
            while (clientBusy || nativeSaving())
            {
                if (Time.realtimeSinceStartup > deadline) { rpc.Invoke(ErrorRpc, id, "A character upload is still in progress."); yield break; }
                yield return null;
            }
            var request = new Capture { Rpc = rpc, Id = id, Logout = logout };
            clientBusy = true;
            try
            {
                capture = request;
                try
                {
                    if (logout) Game.instance.Logout();
                    else Game.instance.SavePlayerProfile(true);
                }
                catch (Exception exception) { request.Error = exception.GetBaseException().Message; }
                finally { capture = null; }
                if (request.Bytes == null && request.Error == null) request.Error = "The game did not serialize a character save.";
                if (request.Error != null)
                {
                    logger.LogError("Character checkpoint failed: " + request.Error);
                    rpc.Invoke(ErrorRpc, id, request.Error);
                }
                else if (!logout)
                {
                    using (var transfer = Send(request).GetEnumerator())
                        while (transfer.MoveNext()) yield return null;
                }
            }
            finally { capture = null; clientBusy = false; }
        }

        private static bool CaptureProfile(byte[] __0, ref byte[] __result)
        {
            if (capture == null) return true;
            __result = __0;
            if (capture.Bytes != null) return false;
            capture.Bytes = __0;
            if (capture.Logout)
            {
                // Use the same synchronous transfer as ServerCharacters' normal logout, before its socket closes.
                try
                {
                    var timer = Stopwatch.StartNew();
                    foreach (bool sent in Send(capture))
                    {
                        if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException("Character upload did not finish.");
                        if (!sent) Thread.Sleep(10);
                    }
                }
                catch (Exception exception) { capture.Error = exception.GetBaseException().Message; }
            }
            return false;
        }

        private static IEnumerable<bool> Send(Capture request)
        {
            if (request.Bytes.Length > MaximumProfileBytes) throw new InvalidOperationException("Character profile exceeds the checkpoint limit.");
            var package = new ZPackage();
            package.Write(request.Id);
            package.Write(request.Bytes);
            var peer = ZNet.instance.GetServerPeer();
            if (peer == null || peer.m_rpc != request.Rpc) throw new InvalidOperationException("Server connection changed while saving.");
            return send(peer, ReplyRpc, package.GetArray());
        }

        private static byte[] ReadSavedPayload(string path)
        {
            var file = new ZPackage(File.ReadAllBytes(path));
            byte[] bytes = file.ReadByteArray(), hash = file.ReadByteArray();
            if (file.GetPos() != file.Size() || !new ZPackage(bytes).GenerateHash().SequenceEqual(hash))
                throw new IOException("Character file failed its checksum: " + Path.GetFileName(path));
            return bytes;
        }

        private static void ReceiveProfile(ZRpc rpc, byte[] envelope)
        {
            if (!running || !pending.TryGetValue(rpc, out var item) || item.Saved != null) return;
            var package = new ZPackage(envelope);
            if (package.ReadString() != operation.id) return;
            byte[] bytes = package.ReadByteArray();
            if (bytes.Length > MaximumProfileBytes || package.GetPos() != package.Size())
                throw new InvalidDataException("Invalid checkpoint payload.");
            var profile = new PlayerProfile(null, FileHelpers.FileSource.Local);
            if (!loadProfile(profile, bytes) || profile.GetName() != item.Name || profile.GetPlayerID() != item.PlayerId)
                throw new InvalidDataException("Checkpoint character identity does not match this connection.");
            acceptingCheckpoint = true;
            PlayerProfile saved;
            try { saved = saveProfile(rpc, bytes); }
            finally { acceptingCheckpoint = false; }
            if (saved == null || !ReadSavedPayload(item.Path).SequenceEqual(bytes))
                throw new IOException("Character save was refused or did not match the requested snapshot.");
            item.Saved = bytes;
            if (Stopping && peerNames.Contains(item.Peer)) inventories.Remove(peerNames[item.Peer]);
            logger.LogInfo("Character checkpoint saved: " + item.Name + ", request " + operation.id + ".");
            WriteStatus();
        }

        // ServerCharacters' saved character file for an account and character name.
        private static string CharacterPath(string account, string name) => Path.Combine(characterDirectory(), account + "_" + name.ToLower() + ".fch");

        internal static bool Available => characterDirectory != null;
        internal static string PrivateSaveDirectory() => new DirectoryInfo(characterDirectory()).Parent.FullName;

        // The account, character name and saved player ID of a connected peer. The ID comes from the character file
        // ServerCharacters keeps for that account and name, read once per connection.
        internal static bool PrivatePeerIdentity(ZRpc rpc, out string account, out string name, out long playerId)
        {
            account = name = null; playerId = 0;
            var peer = ZNet.instance.GetPeers().Find(item => item.m_rpc == rpc);
            if (peer == null || !peer.IsReady() || !peer.m_socket.IsConnected() || forbiddenName(peer.m_playerName)) return false;
            if (!Identities.TryGetValue(rpc, out var known) || known.Name != peer.m_playerName)
            {
                string owner = accountId(peer.m_socket.GetHostName());
                var profile = new PlayerProfile(null, FileHelpers.FileSource.Local);
                if (!loadProfile(profile, ReadSavedPayload(CharacterPath(owner, peer.m_playerName))) || profile.GetName() != peer.m_playerName || profile.GetPlayerID() == 0) return false;
                known = new Identity { Account = owner, Name = peer.m_playerName, PlayerId = profile.GetPlayerID() };
                Identities.Remove(rpc); Identities.Add(rpc, known);
            }
            account = known.Account; name = known.Name; playerId = known.PlayerId;
            return true;
        }

        internal static void Tick()
        {
            var current = ZNet.instance;
            if (runner == null || current == null || !current.IsDedicated() || Game.instance == null || quitting) return;
            float now = Time.realtimeSinceStartup;
            if (network != current)
            {
                network = current;
                pending.Clear(); operation = null; running = false; phase = "idle"; error = "";
                controlDirectory = Path.Combine(Paths.BepInExRootPath, "server-control");
                Directory.CreateDirectory(controlDirectory);
                File.Delete(Path.Combine(controlDirectory, "request.json"));
                nextCheckpoint = now + Plugin.CharacterCheckpointMinutes.Value * 60f;
                WriteStatus();
            }
            if (now < nextPoll) return;
            nextPoll = now + 0.5f;
            try
            {
                if (running) Advance(now);
                else
                {
                    string path = Path.Combine(controlDirectory, "request.json");
                    if (File.Exists(path))
                    {
                        var request = JsonConvert.DeserializeObject<Request>(File.ReadAllText(path));
                        File.Delete(path);
                        if (request == null || request.pid != ProcessId || !Guid.TryParseExact(request.id, "N", out _) ||
                            (request.action != "stop" && request.action != "checkpoint"))
                            throw new InvalidDataException("Invalid or stale server-control request.");
                        Begin(request, now);
                    }
                    else if (Plugin.CharacterCheckpointMinutes.Value > 0 && now >= nextCheckpoint)
                        Begin(new Request { id = Guid.NewGuid().ToString("N"), pid = ProcessId, action = "checkpoint" }, now);
                }
                if (now >= nextStatus) WriteStatus();
            }
            catch (Exception exception) { Fail(exception.GetBaseException().Message); }
        }

        private static void Begin(Request request, float now)
        {
            operation = request; running = true; phase = "saving-characters"; error = ""; savedWorld = 0;
            pending.Clear();
            nextCheckpoint = now + Plugin.CharacterCheckpointMinutes.Value * 60f;
            foreach (var peer in network.GetPeers().ToArray())
            {
                if (!peer.IsReady())
                {
                    if (Stopping) network.Disconnect(peer);
                    continue;
                }
                string account = accountId(peer.m_socket.GetHostName());
                if (forbiddenName(peer.m_playerName)) throw new InvalidDataException("Invalid character name on a connected peer.");
                string path = CharacterPath(account, peer.m_playerName);
                var previous = new PlayerProfile(null, FileHelpers.FileSource.Local);
                if (!loadProfile(previous, ReadSavedPayload(path))) throw new InvalidDataException("Cannot read existing character " + peer.m_playerName);
                pending.Add(peer.m_rpc, new Pending { Peer = peer, Name = peer.m_playerName, Account = account,
                    Path = path, PlayerId = previous.GetPlayerID(), Deadline = now + RequestTimeout });
            }
            WriteStatus();
            foreach (var entry in pending) entry.Key.Invoke(RequestRpc, request.id, request.action == "stop");
            logger.LogInfo("Character checkpoint request " + request.id + ": " + request.action + ", " + pending.Count + " player(s).");
        }

        private static void Advance(float now)
        {
            bool waiting = false;
            foreach (var item in pending.Values)
            {
                if (item.Error != null) throw new IOException(item.Name + ": " + item.Error);
                bool connected = network.GetPeers().Contains(item.Peer) && item.Peer.m_socket.IsConnected();
                if (item.Saved == null && !connected) throw new IOException(item.Name + " disconnected before a fresh character save was confirmed.");
                if (now > item.Deadline && (item.Saved == null || (Stopping && connected)))
                    throw new TimeoutException(item.Name + ": timed out waiting for the fresh save and logout.");
                waiting |= item.Saved == null || (Stopping && connected);
            }
            if (waiting) return;
            if (operation.action == "checkpoint")
            {
                phase = "complete"; running = false; WriteStatus();
                return;
            }
            foreach (var item in pending.Values)
                if (!ReadSavedPayload(item.Path).SequenceEqual(item.Saved))
                    throw new IOException(item.Name + ": the final character file changed after confirmation.");
            if (network.GetPeers().Count != 0) throw new IOException("A connection remains; shutdown refused.");
            if (!network.EnoughDiskSpaceAvailable(out _) || SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveWorld))
                throw new IOException("The game cannot safely save the world; shutdown refused.");
            phase = "saving-world"; WriteStatus();
            uint before = SaveSystem.GetSaveNumber();
            network.Save(true, false, false);
            savedWorld = SaveSystem.GetSaveNumber();
            string directory = ZNet.World.GetSaveDirectory(FileHelpers.FileSource.Local);
            if (savedWorld <= before || !File.Exists(Path.Combine(directory, SaveSystem.MainOKFileName(false))))
                throw new IOException("A new completed world save was not written; shutdown refused.");
            phase = "stopping"; WriteStatus();
            logger.LogInfo("Verified shutdown: " + pending.Count + " character(s) saved and disconnected; world save " + savedWorld + " complete.");
            quitting = true;
            shutdownGame(Game.instance, false);
            Application.Quit();
        }

        private static void Fail(string reason)
        {
            running = false; phase = "failed"; error = reason;
            logger.LogError("Character checkpoint/shutdown failed; server remains running: " + reason);
            WriteStatus();
        }

        private static void WriteStatus()
        {
            if (controlDirectory == null) return;
            var status = new {
                protocol = 1, pid = ProcessId, id = operation?.id, action = operation?.action,
                phase, error, updated = DateTime.UtcNow.ToString("O"), worldSave = savedWorld,
                players = pending.Values.Select(item => new { name = item.Name, account = item.Account,
                    saved = item.Saved != null, error = item.Error }).ToArray()
            };
            // Read only by the server scripts on this machine, every few seconds; it need not wait for the disk.
            WholeFile.Replace(Path.Combine(controlDirectory, "status.json"), Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(status)), false);
            nextStatus = Time.realtimeSinceStartup + 5f;
        }
    }
}
