using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class DungeonOccupancy
    {
        private const string Rpc = Plugin.Id + ".dungeon-position";
        private const string EnterRpc = Plugin.Id + ".dungeon-enter";
        private const string EnteredRpc = Plugin.Id + ".dungeon-enter-ready";
        private static readonly AccessTools.FieldRef<Player, float> Cooldown = AccessTools.FieldRefAccess<Player, float>("m_teleportCooldown");
        private static readonly Dictionary<ZRpc, Presence> positions = new Dictionary<ZRpc, Presence>();
        private static Vector3? destination;
        private static float nextReport;
        private struct Presence { internal Vector3 Position, Destination; }
        private sealed class Entry
        {
            internal string Id;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal bool Distant;
            internal float Requested;
            internal long SummonTransfer;
        }
        private static Entry pending;
        private static bool admitted;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            harmony.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"), postfix: new HarmonyMethod(typeof(DungeonOccupancy), nameof(Register)));
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.TeleportTo)),
                prefix: new HarmonyMethod(typeof(DungeonOccupancy), nameof(ReserveEntry)),
                postfix: new HarmonyMethod(typeof(DungeonOccupancy), nameof(Entering)));
        }

        private static void Register(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer())
            {
                peer.m_rpc.Register<Vector3, Vector3>(Rpc,
                    (rpc, position, target) => positions[rpc] = new Presence { Position = position, Destination = target });
                peer.m_rpc.Register<string, Vector3>(EnterRpc, (rpc, id, target) => {
                    Vector3 position = positions.TryGetValue(rpc, out var previous) ? previous.Position : peer.m_refPos;
                    positions[rpc] = new Presence { Position = position, Destination = target };
                    // Both this callback and regeneration run on the main thread. An acknowledgement cannot pass a reset in progress.
                    rpc.Invoke(EnteredRpc, id);
                });
            }
            else peer.m_rpc.Register<string>(EnteredRpc, Admit);
        }

        private static bool ReserveEntry(Player __instance, Vector3 pos, Quaternion rot, bool distantTeleport, ref bool __result)
        {
            if (!DungeonInteriors.Enabled || __instance != Player.m_localPlayer || admitted || ZNet.instance == null || ZNet.instance.IsServer() || pos.y <= DungeonInteriors.InteriorHeight) return true;
            if (__instance.IsTeleporting() || Cooldown(__instance) < 2) return true;
            var server = ZNet.instance.GetServerPeer();
            if (server == null) return true;
            __result = false;
            if (pending != null) return false;
            pending = new Entry { Id = Guid.NewGuid().ToString("N"), Position = pos, Rotation = rot, Distant = distantTeleport, Requested = Time.realtimeSinceStartup,
                SummonTransfer = SummonDungeonTravel.Context };
            destination = pos;
            server.m_rpc.Invoke(EnterRpc, pending.Id, pos);
            __result = true;
            return false;
        }

        private static void Admit(ZRpc rpc, string id)
        {
            if (pending == null || pending.Id != id || ZNet.instance?.GetServerPeer()?.m_rpc != rpc || Player.m_localPlayer == null) return;
            var entry = pending;
            pending = null;
            admitted = true;
            try
            {
                if (!SummonDungeonTravel.InContext(entry.SummonTransfer, () => Player.m_localPlayer.TeleportTo(entry.Position, entry.Rotation, entry.Distant))) destination = null;
            }
            finally { admitted = false; Report(Player.m_localPlayer.transform.position); }
        }

        private static void Entering(Player __instance, Vector3 pos, bool __result)
        {
            if (!DungeonInteriors.Enabled || __instance != Player.m_localPlayer || !__result) return;
            // Reserve the destination before the teleport animation starts. Keep the old interior occupied during an exit.
            destination = pos.y > DungeonInteriors.InteriorHeight ? pos : (Vector3?)null;
            Report(__instance.transform.position);
        }

        internal static void Tick()
        {
            if (Time.realtimeSinceStartup < nextReport || !DungeonInteriors.Enabled || Player.m_localPlayer == null || ZNet.instance == null) return;
            nextReport = Time.realtimeSinceStartup + 0.5f;
            var player = Player.m_localPlayer;
            if (pending != null && Time.realtimeSinceStartup - pending.Requested > 15)
            {
                SummonDungeonTravel.CancelContext(pending.SummonTransfer);
                pending = null;
                destination = null;
                player.Message(MessageHud.MessageType.Center, "Could not enter the dungeon. Try again.");
            }
            if (pending == null && !player.IsTeleporting()) destination = null;
            Report(player.transform.position);
        }

        private static void Report(Vector3 position)
        {
            var server = ZNet.instance?.GetServerPeer();
            if (server != null) server.m_rpc.Invoke(Rpc, position, destination ?? position);
        }

        internal static bool ContainsPlayer(Vector2s zone)
        {
            var current = ZNet.instance;
            if (current == null) return false;
            var host = Player.m_localPlayer;
            if (host != null && (DungeonInteriors.Inside(host.transform.position, zone) || destination.HasValue && DungeonInteriors.Inside(destination.Value, zone))) return true;
            var peers = current.GetPeers();
            var connected = new HashSet<ZRpc>();
            foreach (var peer in peers)
            {
                connected.Add(peer.m_rpc);
                if (!peer.IsReady()) continue;
                var character = ZDOMan.instance?.GetZDO(peer.m_characterID);
                if (character != null && DungeonInteriors.Inside(character.GetPosition(), zone)) return true;
                if (positions.TryGetValue(peer.m_rpc, out var reported))
                {
                    if (DungeonInteriors.Inside(reported.Position, zone) || DungeonInteriors.Inside(reported.Destination, zone)) return true;
                }
                else if (DungeonInteriors.Inside(peer.m_refPos, zone)) return true;
            }
            foreach (var disconnected in new List<ZRpc>(positions.Keys)) if (!connected.Contains(disconnected)) positions.Remove(disconnected);
            return false;
        }

        internal static void Clear() { positions.Clear(); destination = null; pending = null; admitted = false; nextReport = 0; }
    }
}
