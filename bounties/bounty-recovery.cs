using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using BepInEx.Logging;
using EpicLoot.Adventure;
using EpicLoot.Adventure.Feature;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // EpicLoot 0.14.11 left a new bounty's spawner out of the world save, so a server restart before anyone came near the bounty
    // removed the spawner and the target never appeared. Once per character and world, the owner's game asks the server which of
    // their open bounties have nothing left in the world: no spawner, no living main target and no main-target kill waiting in
    // EpicLoot's ledger for an offline owner. It places a new spawner for each, as accepting the bounty does, so the target
    // appears when someone comes near. The server's answer walks every world object once, so it is asked only once.
    internal static class BountyRecovery
    {
        private const string RequestRpc = Plugin.Id + ".bounty-recovery";
        private const string ReplyRpc = Plugin.Id + ".bounty-recovery-reply";
        private const string CheckedKey = Plugin.Id + ".bounty-recovery";
        private const float ReplySeconds = 30f;
        private const int MaxBounties = 10000;
        private static readonly int SpawnerPrefab = "EL_SpawnController".GetStableHashCode();
        private static readonly int SpawnerBountyKey = "bount_spawn".GetStableHashCode();
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> Objects =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");
        private static readonly Dictionary<int, List<string>> replies = new Dictionary<int, List<string>>();
        private static int lastRequest;
        private static Player recovering;
        private static HashSet<int> creatures;
        private static Action<BountyInfo, Vector3> createSpawner;
        private static ManualLogSource log;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource logger)
        {
            log = logger;
            var spawner = typeof(BountyInfo).Assembly.GetType("EpicLoot.Adventure.AdventureSpawnController", true);
            createSpawner = AccessTools.MethodDelegate<Action<BountyInfo, Vector3>>(AccessTools.Method(spawner, "CreateForBounty"));
            harmony.Patch(AccessTools.Method(typeof(Game), "SpawnPlayer"), postfix: new HarmonyMethod(typeof(BountyRecovery), nameof(Spawned)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"), postfix: new HarmonyMethod(typeof(BountyRecovery), nameof(Register)));
        }

        private static void Spawned()
        {
            var player = Player.m_localPlayer;
            if (player == null || player == recovering || ZNet.instance == null || ZNet.instance.IsDedicated() || Checked(player)) return;
            player.StartCoroutine(Recover(player));
        }

        private static string World => ZNet.instance.GetWorldUID().ToString();

        private static bool Checked(Player player) =>
            player.m_customData.TryGetValue(CheckedKey, out var worlds) && worlds.Split(',').Contains(World);

        private static IEnumerator Recover(Player player)
        {
            recovering = player;
            long owner = player.GetPlayerID();
            var open = player.GetAdventureSaveData().Bounties
                .Where(bounty => bounty.PlayerID == owner && bounty.State == BountyState.InProgress && !bounty.Slain).Select(bounty => bounty.ID).ToList();
            var missing = new List<string>();
            if (open.Count > 0)
            {
                missing = null;
                yield return Query(owner, open, reply => missing = reply);
                if (missing == null)
                {
                    log.LogWarning("The server did not report which open bounties have nothing left in the world; they are checked again on the next join.");
                    recovering = null;
                    yield break;
                }
            }
            // A kill EpicLoot's ledger delivered while the server answered has already marked its bounty slain.
            var saveData = player.GetAdventureSaveData();
            foreach (string id in missing)
            {
                var bounty = saveData.GetBountyInfoByID(id);
                if (bounty == null || bounty.PlayerID != owner || bounty.State != BountyState.InProgress || bounty.Slain) continue;
                var position = (Vector3)bounty.Position;
                createSpawner(bounty, position);
                log.LogInfo($"Restored lost bounty {bounty.ID}, {bounty.TargetName}, at ({position.x:0}, {position.z:0}); its target appears when someone comes near.");
            }
            player.m_customData.TryGetValue(CheckedKey, out var worlds);
            player.m_customData[CheckedKey] = string.IsNullOrEmpty(worlds) ? World : worlds + "," + World;
            recovering = null;
        }

        // The listed bounties of this player that have nothing left in the world, or null without an answer.
        private static IEnumerator Query(long owner, List<string> ids, Action<List<string>> onReply)
        {
            if (ZNet.instance.IsServer())
            {
                onReply(Missing(owner, ids));
                yield break;
            }
            var server = ZNet.instance.GetServerPeer();
            if (server == null) yield break;
            int id = ++lastRequest;
            replies[id] = null;
            var request = new ZPackage();
            request.Write(owner);
            request.Write(ids.Count);
            foreach (string bounty in ids) request.Write(bounty);
            server.m_rpc.Invoke(RequestRpc, id, request);
            float deadline = Time.realtimeSinceStartup + ReplySeconds;
            while (replies[id] == null && Time.realtimeSinceStartup < deadline) yield return null;
            var reply = replies[id];
            replies.Remove(id);
            if (reply != null) onReply(reply);
        }

        private static void Register(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer()) peer.m_rpc.Register<int, ZPackage>(RequestRpc, Answer);
            else peer.m_rpc.Register<int, ZPackage>(ReplyRpc, Receive);
        }

        private static void Answer(ZRpc rpc, int id, ZPackage request)
        {
            long owner = request.ReadLong();
            int count = request.ReadInt();
            if (count < 0 || count > MaxBounties) return;
            var ids = new List<string>(count);
            for (int i = 0; i < count; i++) ids.Add(request.ReadString());
            var missing = Missing(owner, ids);
            var reply = new ZPackage();
            reply.Write(missing.Count);
            foreach (string bounty in missing) reply.Write(bounty);
            rpc.Invoke(ReplyRpc, id, reply);
        }

        private static void Receive(ZRpc rpc, int id, ZPackage package)
        {
            if (!replies.TryGetValue(id, out var pending) || pending != null) return;
            int count = package.ReadInt();
            var missing = new List<string>(count);
            for (int i = 0; i < count; i++) missing.Add(package.ReadString());
            replies[id] = missing;
        }

        // A spawner only this owner's bounties carry is read; one that cannot be read, or a missing ledger, leaves every bounty
        // in place, so an uncertain answer never spawns a second target.
        private static List<string> Missing(long owner, List<string> ids)
        {
            var watch = Stopwatch.StartNew();
            var missing = new HashSet<string>(ids);
            var ledger = BountyManagmentSystem.Instance?.BountyLedger;
            if (ledger == null)
            {
                log.LogWarning("EpicLoot's bounty ledger is not loaded; no lost bounty is restored.");
                return new List<string>();
            }
            foreach (var kill in ledger.GetAllKillLogs(owner) ?? new List<BountyKillLog>())
                if (!kill.IsAdd) missing.Remove(kill.BountyID);
            creatures ??= new HashSet<int>(ZNetScene.instance.m_prefabs.Where(prefab => prefab.GetComponent<Character>()).Select(prefab => prefab.name.GetStableHashCode()));
            byte[] ownerBytes = BitConverter.GetBytes(owner);
            var formatter = new BinaryFormatter();
            foreach (var zdo in Objects(ZDOMan.instance).Values)
            {
                if (missing.Count == 0) break;
                int prefab = zdo.GetPrefab();
                if (prefab == SpawnerPrefab)
                {
                    var data = zdo.GetByteArray(SpawnerBountyKey);
                    if (data == null || !Contains(data, ownerBytes)) continue;
                    try { missing.Remove(((BountyInfo)formatter.Deserialize(new MemoryStream(data))).ID); }
                    catch (Exception error)
                    {
                        log.LogWarning($"Cannot read a bounty spawner at {zdo.GetPosition()}: {error.Message}; no lost bounty is restored.");
                        return new List<string>();
                    }
                }
                else if (creatures.Contains(prefab) && !zdo.GetBool(BountyCreature.IsAddKey))
                {
                    string id = zdo.GetString(BountyCreature.IdKey);
                    if (id.Length == 0 && BountyCreature.TryRead(zdo, out var bounty)) id = bounty.ID;
                    if (id.Length > 0) missing.Remove(id);
                }
            }
            var result = ids.Where(missing.Contains).ToList();
            log.LogInfo($"Checked {ids.Count} open bounties of player {owner} in {watch.ElapsedMilliseconds} ms; {result.Count} have nothing left in the world.");
            return result;
        }

        private static bool Contains(byte[] data, byte[] pattern)
        {
            for (int start = 0; start <= data.Length - pattern.Length; start++)
            {
                int i = 0;
                while (i < pattern.Length && data[start + i] == pattern[i]) i++;
                if (i == pattern.Length) return true;
            }
            return false;
        }
    }
}
