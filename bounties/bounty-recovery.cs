using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        private static readonly int SpawnerIdentity = (Plugin.Id + ".bounty-id").GetStableHashCode();
        private static readonly int SpawnerPrefab = "EL_SpawnController".GetStableHashCode();
        private static readonly int SpawnerBountyKey = "bount_spawn".GetStableHashCode();
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> Objects =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");
        private static readonly Dictionary<int, (List<string> Missing, bool Complete)> replies = new();
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
            harmony.Patch(AccessTools.Method(spawner, "SetBounty"), postfix: new HarmonyMethod(typeof(BountyRecovery), nameof(RememberIdentity)));
            harmony.Patch(AccessTools.Method(typeof(Game), "SpawnPlayer"), postfix: new HarmonyMethod(typeof(BountyRecovery), nameof(Spawned)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"), postfix: new HarmonyMethod(typeof(BountyRecovery), nameof(Register)));
        }

        private static void RememberIdentity(Component __instance, BountyInfo __0)
        {
            var view = __instance.GetComponent<ZNetView>();
            if (view && view.IsValid() && view.IsOwner()) view.GetZDO().Set(SpawnerIdentity, __0.ID);
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
                .Where(bounty => bounty.PlayerID == owner && bounty.State == BountyState.InProgress && !bounty.Slain).ToList();
            var missing = new List<string>();
            bool conclusive = true;
            if (open.Count > 0)
            {
                missing = null;
                yield return Query(owner, open, (ids, complete) => { missing = ids; conclusive = complete; });
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
            if (conclusive)
            {
                player.m_customData.TryGetValue(CheckedKey, out var worlds);
                player.m_customData[CheckedKey] = string.IsNullOrEmpty(worlds) ? World : worlds + "," + World;
            }
            recovering = null;
        }

        // The listed bounties of this player that have nothing left in the world, or null without an answer.
        private static IEnumerator Query(long owner, List<BountyInfo> bounties, Action<List<string>, bool> onReply)
        {
            var positions = bounties.ToDictionary(bounty => bounty.ID, bounty => (Vector3)bounty.Position);
            if (ZNet.instance.IsServer())
            {
                var result = Missing(owner, positions, out bool complete);
                onReply(result, complete);
                yield break;
            }
            var server = ZNet.instance.GetServerPeer();
            if (server == null) yield break;
            int id = ++lastRequest;
            replies[id] = default;
            var request = new ZPackage();
            request.Write(owner);
            request.Write(positions.Count);
            foreach (var bounty in positions) { request.Write(bounty.Key); request.Write(bounty.Value); }
            server.m_rpc.Invoke(RequestRpc, id, request);
            float deadline = Time.realtimeSinceStartup + ReplySeconds;
            while (replies.TryGetValue(id, out var answer) && answer.Missing == null && Time.realtimeSinceStartup < deadline) yield return null;
            replies.TryGetValue(id, out var reply);
            replies.Remove(id);
            if (reply.Missing != null) onReply(reply.Missing, reply.Complete);
        }

        private static void Register(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer()) peer.m_rpc.Register<int, ZPackage>(RequestRpc, Answer);
            else peer.m_rpc.Register<int, ZPackage>(ReplyRpc, Receive);
        }

        private static void Answer(ZRpc rpc, int id, ZPackage request)
        {
            if (ZNet.instance.GetPeers().Find(candidate => candidate.m_rpc == rpc)?.IsReady() != true) return;
            long owner = request.ReadLong();
            int count = request.ReadInt();
            if (count < 0 || count > MaxBounties) return;
            var positions = new Dictionary<string, Vector3>();
            for (int i = 0; i < count; i++) positions[request.ReadString()] = request.ReadVector3();
            var missing = Missing(owner, positions, out bool complete);
            var reply = new ZPackage();
            reply.Write(complete);
            reply.Write(missing?.Count ?? -1);
            if (missing != null) foreach (string bounty in missing) reply.Write(bounty);
            rpc.Invoke(ReplyRpc, id, reply);
        }

        private static void Receive(ZRpc rpc, int id, ZPackage package)
        {
            if (ZNet.instance?.GetServerPeer()?.m_rpc != rpc || !replies.TryGetValue(id, out var pending) || pending.Missing != null) return;
            bool complete = package.ReadBool();
            int count = package.ReadInt();
            if (count < 0) { replies.Remove(id); return; }
            if (count > MaxBounties) return;
            var missing = new List<string>(count);
            for (int i = 0; i < count; i++) missing.Add(package.ReadString());
            replies[id] = (missing, complete);
        }

        // A spawner only this owner's bounties carry is read; one that cannot be read, or a missing ledger, leaves every bounty
        // in place, so an uncertain answer never spawns a second target.
        private static List<string> Missing(long owner, Dictionary<string, Vector3> positions, out bool complete)
        {
            complete = false;
            var watch = Stopwatch.StartNew();
            var missing = new HashSet<string>(positions.Keys);
            var ledger = BountyManagmentSystem.Instance?.BountyLedger;
            if (ledger == null)
            {
                log.LogWarning("EpicLoot's bounty ledger is not loaded; no lost bounty is restored.");
                return null;
            }
            foreach (var kill in ledger.GetAllKillLogs(owner) ?? new List<BountyKillLog>())
                if (!kill.IsAdd) missing.Remove(kill.BountyID);
            creatures ??= new HashSet<int>(ZNetScene.instance.m_prefabs.Where(prefab => prefab.GetComponent<Character>()).Select(prefab => prefab.name.GetStableHashCode()));
            bool uncertain = false;
            foreach (var zdo in Objects(ZDOMan.instance).Values)
            {
                if (missing.Count == 0) break;
                int prefab = zdo.GetPrefab();
                if (prefab == SpawnerPrefab)
                {
                    string id = zdo.GetString(SpawnerIdentity);
                    if (id.Length > 0) { missing.Remove(id); continue; }
                    // EpicLoot places each legacy controller at the exact position stored in its contract.
                    // A controller there without a readable identity is evidence against restoring the contract.
                    if (zdo.GetByteArray(SpawnerBountyKey) == null) continue;
                    var at = zdo.GetPosition();
                    foreach (var pair in positions)
                        if ((pair.Value - at).sqrMagnitude < .01f && missing.Remove(pair.Key)) uncertain = true;
                }
                else if (creatures.Contains(prefab) && !zdo.GetBool(BountyCreature.IsAddKey))
                {
                    string id = zdo.GetString(BountyCreature.IdKey);
                    if (id.Length == 0 && BountyCreature.TryRead(zdo, out var bounty)) id = bounty.ID;
                    if (id.Length > 0) missing.Remove(id);
                }
            }
            var result = positions.Keys.Where(missing.Contains).ToList();
            log.LogInfo($"Checked {positions.Count} open bounties of player {owner} in {watch.ElapsedMilliseconds} ms; {result.Count} have nothing left in the world.");
            complete = !uncertain;
            return result;
        }

    }
}
