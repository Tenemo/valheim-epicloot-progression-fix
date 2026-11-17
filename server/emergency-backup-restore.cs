using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // A player whose connection drops keeps playing until the game notices, then saves a signed emergency backup and
    // sends it on the next join. ServerCharacters accepts it only if its key, issued when that connection began, is newer
    // than the character file. The server rewrites that file at every ten-minute save, so after a session's first
    // minutes every backup was refused and the player rejoined where the last save had them. The server now also
    // accepts a backup signed with the key of the account's connection that ended last, once, until that account
    // sends a newer save. An older backup, or one from before a server restart, still goes through ServerCharacters'
    // own rule, which keeps the world and the character from different moments apart after a crash.
    internal static class EmergencyBackupRestore
    {
        private static readonly Dictionary<ZNetPeer, KeyValuePair<string, long>> sessionKeys = new Dictionary<ZNetPeer, KeyValuePair<string, long>>();
        private static readonly Dictionary<string, long> endedKeys = new Dictionary<string, long>();
        private static Func<string, string> accountId;
        private static ManualLogSource logger;
        private static bool issuing;
        private static long issued;
        private static Restore restore;

        private sealed class Restore
        {
            internal string Account;
            internal long Key;
            internal bool Admitted;
        }

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.ServerCharacters, out var plugin)) return;
            logger = log;
            var assembly = plugin.Instance.GetType().Assembly;
            var connection = assembly.GetType("ServerCharacters.ServerSide+PatchZNetOnNewConnection", true);
            accountId = AccessTools.MethodDelegate<Func<string, string>>(AccessTools.Method(assembly.GetType("ServerCharacters.Utils", true), "GetPlayerID"));
            harmony.Patch(AccessTools.Method(connection, "Postfix"),
                prefix: new HarmonyMethod(typeof(EmergencyBackupRestore), nameof(StartIssuing)),
                postfix: new HarmonyMethod(typeof(EmergencyBackupRestore), nameof(RecordIssued)));
            harmony.Patch(AccessTools.Method(connection, "deriveKey"),
                postfix: new HarmonyMethod(typeof(EmergencyBackupRestore), nameof(RecordKey)));
            harmony.Patch(AccessTools.Method(connection, "onReceivedSignature"),
                prefix: new HarmonyMethod(typeof(EmergencyBackupRestore), nameof(ReadSignature)),
                finalizer: new HarmonyMethod(typeof(EmergencyBackupRestore), nameof(FinishRestore)),
                transpiler: new HarmonyMethod(typeof(EmergencyBackupRestore), nameof(CompareWithSession)));
            harmony.Patch(AccessTools.Method(connection, "onReceivedProfile"),
                prefix: new HarmonyMethod(typeof(EmergencyBackupRestore), nameof(ForgetEndedSession)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "Disconnect"),
                prefix: new HarmonyMethod(typeof(EmergencyBackupRestore), nameof(EndSession)));
            logger.LogInfo("Emergency backup restore enabled for ServerCharacters " + plugin.Metadata.Version + ".");
        }

        private static string Account(ISocket socket) => socket == null ? null : accountId(socket.GetHostName());

        // ServerCharacters derives a connection's key from the time it issues it and sends both to the player.
        private static void StartIssuing()
        {
            issuing = true;
            issued = 0;
        }

        private static void RecordKey(long time)
        {
            if (issuing) issued = time;
        }

        private static void RecordIssued(object[] __args)
        {
            issuing = false;
            if (!(__args[1] is ZNetPeer peer) || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (issued == 0)
                DisabledFeatures.Set("Emergency backup restore", "ServerCharacters issued a connection key without deriving it.");
            // The account is read now: once the connection has closed, its socket no longer names the player.
            else if (Account(peer.m_socket) is string account)
                sessionKeys[peer] = new KeyValuePair<string, long>(account, issued);
        }

        private static void EndSession(ZNet __instance, ZNetPeer peer)
        {
            if (!__instance.IsServer() || peer == null || !sessionKeys.TryGetValue(peer, out var session)) return;
            sessionKeys.Remove(peer);
            endedKeys[session.Key] = session.Value;
        }

        // A save from any later connection of the account is newer than every backup of the one that ended.
        private static void ForgetEndedSession(ZRpc peerRpc)
        {
            string account = Account(peerRpc?.GetSocket());
            if (account != null) endedKeys.Remove(account);
        }

        private static void ReadSignature(ZRpc peerRpc, byte[] signedProfile)
        {
            restore = null;
            try
            {
                var package = new ZPackage(signedProfile);
                package.ReadByteArray();
                var signature = new ZPackage(package.ReadByteArray());
                signature.ReadByteArray();
                signature.ReadByteArray();
                restore = new Restore { Account = Account(peerRpc?.GetSocket()), Key = signature.ReadLong() };
            }
            catch (Exception)
            {
                // ServerCharacters rejects the same malformed package itself.
            }
        }

        private static IEnumerable<CodeInstruction> CompareWithSession(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(AccessTools.PropertyGetter(typeof(FileSystemInfo), nameof(FileSystemInfo.LastWriteTime))),
                AccessTools.Method(typeof(EmergencyBackupRestore), nameof(SavedBefore)), 1,
                "ServerCharacters' emergency backup age check changed.");

        // Runs after ServerCharacters has verified the signature, so the key time is the server's own.
        private static DateTime SavedBefore(FileSystemInfo file)
        {
            var current = restore;
            if (current?.Account != null && endedKeys.TryGetValue(current.Account, out long key) && key == current.Key)
            {
                current.Admitted = true;
                logger.LogInfo($"Restoring {current.Account}'s emergency backup from the connection that ended last.");
                return DateTime.MinValue;
            }
            return file.LastWriteTime;
        }

        private static Exception FinishRestore(Exception __exception)
        {
            if (restore != null && restore.Admitted) endedKeys.Remove(restore.Account);
            restore = null;
            return __exception;
        }
    }
}
