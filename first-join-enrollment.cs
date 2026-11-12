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
    internal static class FirstJoinEnrollment
    {
        private const string EnrollmentRequestRpc = Plugin.Id + ".enroll-request";
        private const string EnrollmentUploadRpc = Plugin.Id + ".enroll-profile";
        private const string ProfileMessage = "ServerCharacters PlayerProfile";
        private static readonly Dictionary<ZRpc, PendingEnrollment> Pending = new();
        private static readonly FieldInfo WorldData = AccessTools.Field(typeof(PlayerProfile), "m_worldData");
        private static readonly Func<PlayerProfile, ZPackage> ReadProfile = AccessTools.MethodDelegate<Func<PlayerProfile, ZPackage>>(
            AccessTools.Method(typeof(PlayerProfile), "LoadPlayerDataFromDisk"));
        private static Func<ZNetPeer, string, byte[], IEnumerable<bool>> send;
        private static Func<Action<ZRpc, byte[]>, Action<ZRpc, ZPackage>> receive;
        private static Func<PlayerProfile, byte[], bool> loadProfile;
        private static Func<string, bool> forbiddenName;
        private static Func<string, string> accountId;
        private static Func<string> characterDirectory;
        private static ManualLogSource logger;

        private sealed class PendingEnrollment
        {
            internal ZNetPeer Peer;
            internal string Account;
            internal long World;
            internal byte[] Data;
            internal bool Failed;
        }

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.ServerCharacters, out var plugin)) return;
            logger = log;
            var assembly = plugin.Instance.GetType().Assembly;
            var shared = assembly.GetType("ServerCharacters.Shared", true);
            var utils = assembly.GetType("ServerCharacters.Utils", true);
            var sendMethod = AccessTools.Method(shared, "sendCompressedDataToPeer");
            send = AccessTools.MethodDelegate<Func<ZNetPeer, string, byte[], IEnumerable<bool>>>(sendMethod);
            receive = AccessTools.MethodDelegate<Func<Action<ZRpc, byte[]>, Action<ZRpc, ZPackage>>>(
                AccessTools.Method(shared, "receiveCompressedFromPeer"));
            loadProfile = AccessTools.MethodDelegate<Func<PlayerProfile, byte[], bool>>(
                AccessTools.Method(shared, "LoadPlayerProfileFromBytes"));
            forbiddenName = AccessTools.MethodDelegate<Func<string, bool>>(AccessTools.Method(shared, "CharacterNameIsForbidden"));
            accountId = AccessTools.MethodDelegate<Func<string, string>>(AccessTools.Method(utils, "GetPlayerID"));
            characterDirectory = AccessTools.MethodDelegate<Func<string>>(AccessTools.PropertyGetter(utils, "CharacterSavePath"));
            if (WorldData == null) throw new MissingFieldException("Player world history is unavailable.");
            harmony.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"),
                postfix: new HarmonyMethod(typeof(FirstJoinEnrollment), nameof(Register)) { after = new[] { PluginIds.ServerCharacters } });
            harmony.Patch(sendMethod, prefix: new HarmonyMethod(typeof(FirstJoinEnrollment), nameof(BeforeSend)));
            var nativeSave = AccessTools.Method(assembly.GetType("ServerCharacters.ServerSide+PatchZNetOnNewConnection", true),
                "onReceivedProfile");
            harmony.Patch(nativeSave, prefix: new HarmonyMethod(typeof(FirstJoinEnrollment), nameof(BlockEarlySave)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "Shutdown"),
                prefix: new HarmonyMethod(typeof(FirstJoinEnrollment), nameof(ClearPending)));
        }

        private static bool HasHistory(PlayerProfile profile, long world) =>
            world != 0 && ((IDictionary)WorldData.GetValue(profile)).Contains(world);

        private static bool HasAccountSave(string account) => Directory.Exists(characterDirectory()) &&
            Directory.EnumerateFiles(characterDirectory(), account + "_*.fch").Any();

        private static string InitialCopy(string account) => Path.Combine(characterDirectory(), "initial-imports", account + ".fch");

        private static bool IsEnrolled(string account)
        {
            string path = Path.Combine(characterDirectory(), "enrollment-accounts.txt");
            return account.StartsWith("Steam_", StringComparison.Ordinal) && account.Length == 23 &&
                account.Substring(6).All(char.IsDigit) && File.Exists(path) &&
                File.ReadLines(path).Any(line => line.Trim() == account);
        }

        private static void Register(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer())
            {
                var handler = receive(AcceptProfile);
                // Only an authenticated peer invited by the server can enter the decompression path.
                peer.m_rpc.Register<ZPackage>(EnrollmentUploadRpc, (rpc, package) => {
                    if (Pending.TryGetValue(rpc, out var pending) && !pending.Failed && pending.Data == null)
                    {
                        try { handler(rpc, package); }
                        catch (Exception error) { Reject(pending, "Invalid enrollment upload: " + error.GetBaseException().Message); }
                    }
                });
            }
            else if (peer.m_server)
                peer.m_rpc.Register<long>(EnrollmentRequestRpc, (rpc, world) => SendLocalProfile(__instance, peer, rpc, world));
        }

        private static bool BeforeSend(ZNetPeer peer, string eventname, byte[] packageArray, ref IEnumerable<bool> __result)
        {
            if (!Plugin.Ready || !ZNet.instance.IsServer() || eventname != ProfileMessage || packageArray.Length != 0 ||
                peer.m_uid == 0) return true;
            string account = accountId(peer.m_rpc.GetSocket().GetHostName());
            try
            {
                if (!IsEnrolled(account) || HasAccountSave(account)) return true;
            }
            catch (Exception error)
            {
                // An empty profile would start a fresh character and end this account's first enrollment, so refuse the join.
                logger.LogError("Enrollment check failed for " + account + ": " + error.GetBaseException().Message);
                Reject(new PendingEnrollment { Peer = peer, Account = account },
                    "The server could not check character enrollment. Nothing was changed; try again shortly.");
                __result = Enumerable.Empty<bool>();
                return false;
            }
            __result = Enroll(peer, account);
            return false;
        }

        private static IEnumerable<bool> Enroll(ZNetPeer peer, string account)
        {
            var pending = new PendingEnrollment { Peer = peer, Account = account, World = ZNet.instance.GetWorldUID() };
            if (Pending.Values.Any(item => item.Account == account) || File.Exists(InitialCopy(account)))
            {
                Reject(pending, "An enrollment already exists or is in progress; reconnect or ask the owner to restore its initial backup.");
                yield break;
            }
            Pending.Add(peer.m_rpc, pending);
            try
            {
                // ServerCharacters buffers PeerInfo until this sequence completes, so the player cannot spawn yet.
                peer.m_rpc.Invoke(EnrollmentRequestRpc, pending.World);
                float deadline = Time.realtimeSinceStartup + 45f;
                while (!pending.Failed && pending.Data == null && peer.m_socket.IsConnected() && Time.realtimeSinceStartup < deadline)
                    yield return false;
                if (pending.Failed) yield break;
                if (pending.Data == null)
                {
                    Reject(pending, "Character enrollment did not complete. The local character has not been replaced.");
                    yield break;
                }
                // A nonempty profile follows the normal authoritative load path, including crafting/favorites initialization.
                foreach (bool ready in send(peer, ProfileMessage, pending.Data)) yield return ready;
            }
            finally { Pending.Remove(peer.m_rpc); }
        }

        private static void SendLocalProfile(ZNet network, ZNetPeer peer, ZRpc rpc, long world)
        {
            // GetServerPeer returns null until PeerInfo completes, which enrollment intentionally holds back.
            // Use the actual outbound connection captured when its RPC handlers were registered.
            if (network != ZNet.instance || network.IsServer() || !peer.m_server || peer.m_rpc != rpc ||
                !network.GetPeers().Contains(peer) || !peer.m_socket.IsConnected()) return;
            try
            {
                logger.LogInfo("Received first-join enrollment request; reading the selected campaign character.");
                var selected = Game.instance.GetPlayerProfile();
                byte[] data = ReadProfile(selected)?.GetArray();
                var stored = new PlayerProfile(null, FileHelpers.FileSource.Local);
                if (data == null || !loadProfile(stored, data) || GameFields.ProfileData(stored) == null ||
                    stored.GetPlayerID() != selected.GetPlayerID() || stored.GetName() != selected.GetName() || !HasHistory(stored, world))
                    throw new InvalidDataException("Select the existing character that has already played in this campaign.");
                network.StartCoroutine(UploadProfile(peer, data));
            }
            catch (Exception error)
            {
                logger.LogError("Character enrollment cancelled: " + error.GetBaseException().Message);
                network.StartCoroutine(UploadProfile(peer, Array.Empty<byte>()));
            }
        }

        private static IEnumerator UploadProfile(ZNetPeer peer, byte[] data)
        {
            foreach (bool ready in send(peer, EnrollmentUploadRpc, data)) if (!ready) yield return null;
        }

        private static void AcceptProfile(ZRpc rpc, byte[] data)
        {
            if (!Pending.TryGetValue(rpc, out var pending) || pending.Failed || pending.Data != null) return;
            try
            {
                if (!Plugin.Ready || !pending.Peer.m_socket.IsConnected() || pending.Peer.m_uid == 0 ||
                    accountId(rpc.GetSocket().GetHostName()) != pending.Account || !IsEnrolled(pending.Account) ||
                    HasAccountSave(pending.Account) || File.Exists(InitialCopy(pending.Account)))
                    throw new InvalidOperationException("This account is no longer eligible for a first enrollment.");
                var profile = new PlayerProfile(null, FileHelpers.FileSource.Local);
                if (data.Length == 0 || !loadProfile(profile, data) || GameFields.ProfileData(profile) == null || GameFields.ProfileData(profile).Length == 0 ||
                    forbiddenName(profile.GetName()) || profile.GetName() != pending.Peer.m_playerName ||
                    profile.GetPlayerID() == 0 || !HasHistory(profile, pending.World))
                    throw new InvalidDataException("Select your existing campaign character; no saved data was replaced.");
                string backup = InitialCopy(pending.Account);
                string target = Path.Combine(characterDirectory(), pending.Account + "_" + profile.GetName().ToLower() + ".fch");
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                // Preserve the original serialized profile without re-saving or rewriting any item, skill or custom data.
                WriteNewProfile(backup, data);
                WriteNewProfile(target, data);
                pending.Data = data;
                logger.LogInfo("Enrolled existing campaign character for " + pending.Account + "; original retained in initial-imports.");
            }
            catch (Exception error) { Reject(pending, "Character enrollment failed: " + error.GetBaseException().Message); }
        }

        // Write the complete file beside its destination, then move it into place without replacing anything.
        // An interrupted write leaves only the temporary file, never a partial profile or backup.
        private static void WriteNewProfile(string path, byte[] data)
        {
            string temporary = path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(file))
            {
                byte[] hash = new ZPackage(data).GenerateHash();
                writer.Write(data.Length);
                writer.Write(data);
                writer.Write(hash.Length);
                writer.Write(hash);
                writer.Flush();
                file.Flush(true);
            }
            File.Move(temporary, path);
        }

        private static bool BlockEarlySave(ZRpc peerRpc) => !Pending.ContainsKey(peerRpc);

        private static void ClearPending() => Pending.Clear();

        private static void Reject(PendingEnrollment pending, string reason)
        {
            pending.Failed = true;
            logger.LogError("Enrollment for " + pending.Account + ": " + reason);
            pending.Peer.m_rpc.Invoke("ServerCharacters KickMessage", reason);
            pending.Peer.m_rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorConnectFailed);
            ZNet.instance.Disconnect(pending.Peer);
        }
    }
}
