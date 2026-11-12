using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class SummonDungeonTravel
    {
        private const string RequestRpc = Plugin.Id + ".summon-dungeon-request", DeliveryRpc = Plugin.Id + ".summon-dungeon-delivery";
        // One completed transfer identity survives ownership changes; pending leases themselves are never saved.
        private const string MovedKey = Plugin.Id + ".dungeon-move";
        private const string BatExpiryKey = Plugin.Id + ".dungeon-bat-expiry";
        private const string SettlingKey = Plugin.Id + ".dungeon-settling";
        private const float Lifetime = 30f, DoorRange = 10f, ArrivalRange = 3f, Step = 1f, SearchRadius = 10f;
        private static readonly int FloorMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece", "piece_nonsolid");
        private static readonly int SolidMask = LayerMask.GetMask("Default", "static_solid", "piece", "piece_nonsolid");
        private enum Request : byte { Begin, Arrived, Cancel, Prepared, Moved, NeedGeometry, Rejected }
        private enum Delivery : byte { Prepare, Enter, Move, End }
        private enum Result : byte { Pending, Moved, Gone }
        private sealed class Route
        {
            internal ZDOID Proxy;
            internal int Door, Target;
            internal Vector3 From, To;
            internal Quaternion Rotation;
            internal Vector2s Zone;
        }
        private sealed class Target
        {
            internal ZDOID Id;
            internal long PrepareSent, Prepared, MoveSent;
            internal ushort PrepareRevision;
            internal uint MinimumRevision;
            internal Result Result;
            internal Result Reported;
            internal bool HandedOver;
            internal Vector3 Center;
            internal float Radius;
            internal long TimerExpiry;
        }
        private sealed class Trip
        {
            internal long Token, Request, Player, Peer;
            internal ZDOID Actor;
            internal string Name;
            internal Route Route;
            internal float Created, Expires;
            internal long Lease;
            internal bool Entered, ArrivalReported, Arrived;
            internal readonly List<Target> Targets = new List<Target>();
            internal readonly HashSet<long> Recipients = new HashSet<long>();
            internal readonly HashSet<ZRpc> Connections = new HashSet<ZRpc>();
        }
        private sealed class LocalTrip
        {
            internal long Request, Token;
            internal ZDOID Actor;
            internal Teleport Door;
            internal Route Route;
            internal float Expires;
            internal bool Entered, Started, Reported, Ended;
            internal readonly HashSet<ZDOID> Selected = new HashSet<ZDOID>();
        }
        private sealed class Held
        {
            internal long Token, Player, Lease;
            internal ZDOID Actor, Id;
            internal string Name;
            internal Route Route;
            internal bool Prepared, Move, Done, NeedsGeometry;
            internal long TimerExpiry;
            internal readonly List<(Vector3 Center, float Radius)> Occupied = new List<(Vector3, float)>();
        }
        private static readonly Dictionary<long, Trip> Trips = new Dictionary<long, Trip>();
        private static readonly Dictionary<long, long> LastRequests = new Dictionary<long, long>();
        private static readonly Dictionary<ZDOID, Held> HeldCreatures = new Dictionary<ZDOID, Held>();
        private static readonly HashSet<long> Closed = new HashSet<long>();
        private static readonly Queue<(long Token, float Until)> Closing = new Queue<(long, float)>();
        private sealed class Timer { internal float Deadline; }
        private static readonly ConditionalWeakTable<TimedDestruction, Timer> BatTimers = new ConditionalWeakTable<TimedDestruction, Timer>();
        private static readonly Func<ZoneSystem, int, ZoneSystem.ZoneLocation> LocationPrefab = AccessTools.MethodDelegate<Func<ZoneSystem, int, ZoneSystem.ZoneLocation>>(AccessTools.Method(typeof(ZoneSystem), "GetLocation", new[] { typeof(int) }));
        private static readonly AccessTools.FieldRef<BaseAI, List<Vector3>> Path = AccessTools.FieldRefAccess<BaseAI, List<Vector3>>("m_path");
        private static readonly AccessTools.FieldRef<BaseAI, float> LastPath = AccessTools.FieldRefAccess<BaseAI, float>("m_lastFindPathTime");
        private static readonly AccessTools.FieldRef<MonsterAI, Character> CombatTarget = AccessTools.FieldRefAccess<MonsterAI, Character>("m_targetCreature");
        private static readonly AccessTools.FieldRef<Player, float> TeleportCooldown = AccessTools.FieldRefAccess<Player, float>("m_teleportCooldown");
        private static readonly Action<BaseAI> ResetPatrol = AccessTools.MethodDelegate<Action<BaseAI>>(AccessTools.Method(typeof(BaseAI), "ResetPatrolPoint"));
        private static readonly Action<BaseAI> SetPatrol = AccessTools.MethodDelegate<Action<BaseAI>>(AccessTools.Method(typeof(BaseAI), "SetPatrolPoint", Type.EmptyTypes));
        private static LocalTrip local;
        private static long requestSequence, tokenSequence;
        private static bool replaying;
        private static float nextServerTick;
        [ThreadStatic] private static long context;

        internal static long Context => context;
        internal static bool InContext(long token, Func<bool> action)
        {
            long previous = context; context = token;
            try { return action(); }
            finally { context = previous; }
        }

        internal static void Initialize(IntegrationPatches patches)
        {
            HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(SummonDungeonTravel), name);
            patches.Patch(AccessTools.Method(typeof(Teleport), nameof(Teleport.Interact)), prefix: Hook(nameof(Interact)));
            patches.Patch(AccessTools.Method(typeof(Player), nameof(Player.TeleportTo)), postfix: Hook(nameof(TeleportAttempted)));
            patches.Patch(AccessTools.Method(typeof(Tameable), "UpdateSummon"), prefix: Hook(nameof(KeepSelectedSummon)));
            patches.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"), postfix: Hook(nameof(Register)));
            patches.Patch(AccessTools.Method(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) }), prefix: Hook(nameof(Disconnecting)));
            patches.Patch(AccessTools.Method(typeof(ZNet), "Shutdown"), postfix: Hook(nameof(Clear)));
            patches.Patch(AccessTools.Method(typeof(ZNetScene), "RemoveObjects"), prefix: Hook(nameof(KeepReplicas)));
            patches.Patch(AccessTools.Method(typeof(CreatureOwnership), nameof(CreatureOwnership.CanOwn)), postfix: Hook(nameof(KeepTransferOwner)));
            patches.Patch(AccessTools.Method(typeof(TimedDestruction), nameof(TimedDestruction.Trigger), Type.EmptyTypes), postfix: Hook(nameof(TimerStarted)));
            patches.Patch(AccessTools.Method(typeof(TimedDestruction), nameof(TimedDestruction.Trigger), new[] { typeof(float) }), postfix: Hook(nameof(TimerStartedWithDelay)));
            patches.Patch(AccessTools.Method(typeof(TimedDestruction), "Awake"), postfix: Hook(nameof(RestoreBatTimer)));
        }

        private static bool Finite(Vector3 value) => !float.IsNaN(value.sqrMagnitude) && !float.IsInfinity(value.sqrMagnitude);
        private static bool BatTimer(TimedDestruction timer) => timer.GetComponent<Character>() && Utils.GetPrefabName(timer.gameObject) == "EL_TamedBat";
        private static void TimerStarted(TimedDestruction __instance) => TimerStartedWithDelay(__instance, __instance.m_timeout);
        private static void TimerStartedWithDelay(TimedDestruction __instance, float timeout)
        { if (BatTimer(__instance)) BatTimers.GetValue(__instance, _ => new Timer()).Deadline = Time.time + timeout; }
        private static void RestoreBatTimer(TimedDestruction __instance)
        {
            if (!BatTimer(__instance) || !ZNet.instance) return;
            var data = __instance.GetComponent<ZNetView>()?.GetZDO();
            if (data != null) ApplyBatTimer(__instance, data.GetLong(BatExpiryKey));
        }
        private static void ApplyBatTimer(TimedDestruction timer, long expiry)
        {
            if (expiry <= 0 || !BatTimers.TryGetValue(timer, out var current)) return;
            float remaining = Mathf.Max(0f, (expiry - ZNet.instance.GetTime().Ticks) / (float)TimeSpan.TicksPerSecond);
            if (current.Deadline <= Time.time + remaining + .02f) return;
            timer.CancelInvoke("DestroyNow"); timer.Trigger(remaining);
        }
        private static bool Alive(ZDO data) => data != null && !data.GetBool(ZDOVars.s_dead) && data.GetFloat(ZDOVars.s_health, 1f) > 0f;
        private static bool StaffFollower(ZDO data) => SummonOwnership.StaffFor(data) is string staff &&
            (staff == SummonOwnership.SkeletonStaff || staff == SummonOwnership.SpiritStaff);
        private static bool Belongs(ZDO data, long player, string name)
        {
            if (!Alive(data) || TameRiding.HoldingPosition(data)) return false;
            long bat = SummonedBats.SummonerId(data);
            if (bat != 0) return bat == player;
            if (!StaffFollower(data) || !data.GetBool(ZDOVars.s_tamed) || data.GetString(ZDOVars.s_follow) != name) return false;
            if (SummonOwnership.Recorded(data, player, SummonOwnership.StaffFor(data))) return true;
            if (!SummonOwnership.Unrecorded(data)) return false;
            var candidates = ZNet.instance.IsServer()
                ? ZNet.instance.GetPeers().Where(peer => peer.IsReady() && peer.m_playerName == name)
                    .Select(peer => ZDOMan.instance.GetZDO(peer.m_characterID)?.GetLong(ZDOVars.s_playerID) ?? 0L).ToList()
                : Player.GetAllPlayers().Where(actor => actor.GetPlayerName() == name).Select(actor => actor.GetPlayerID()).ToList();
            if (ZNet.instance.IsServer() && Player.m_localPlayer && Player.m_localPlayer.GetPlayerName() == name) candidates.Add(Player.m_localPlayer.GetPlayerID());
            return candidates.Count == 1 && candidates[0] == player;
        }
        private static bool SameSide(Vector3 position, Route route) => (position.y > DungeonInteriors.InteriorHeight) == (route.From.y > DungeonInteriors.InteriorHeight) &&
            (position.y <= DungeonInteriors.InteriorHeight || DungeonInteriors.Inside(position, route.Zone));
        private static bool Follows(Character creature, Player player)
        {
            var view = GameFields.CharacterView(creature); var following = creature.GetComponent<MonsterAI>()?.GetFollowTarget();
            if (!view || !view.IsValid()) return false;
            if (SummonedBats.SummonerId(creature) == player.GetPlayerID()) return !view.IsOwner() || !following || following == player.gameObject;
            return view.GetZDO().GetString(ZDOVars.s_follow) == player.GetPlayerName() && (!view.IsOwner() || !following || following == player.gameObject);
        }

        // Read the same prefab transforms that SpawnLocation uses, without instantiating or changing the asset.
        private static Matrix4x4 DoorMatrix(Transform door, Transform root, Location location, Vector3 position, Quaternion rotation, out Quaternion worldRotation)
        {
            var hierarchy = new List<Transform>();
            for (var part = door; part != root; part = part.parent)
            { if (!part) throw new InvalidOperationException("A dungeon doorway is outside its location prefab."); hierarchy.Add(part); }
            hierarchy.Reverse();
            bool custom = location.m_useCustomInteriorTransform && location.m_interiorTransform && location.m_generator;
            Vector3 interior = Vector3.zero;
            if (custom)
            {
                var offset = location.m_interiorTransform.localPosition;
                var shift = ZoneSystem.GetZonePos(ZoneSystem.GetZone(position)) + offset + location.m_generator.transform.localPosition - position;
                interior = Quaternion.Inverse(rotation) * shift; interior.y = offset.y;
            }
            var matrix = Matrix4x4.TRS(position, rotation, root.localScale);
            worldRotation = rotation;
            foreach (var part in hierarchy)
            {
                var translation = part.localPosition; var orientation = part.localRotation;
                if (custom && part == location.m_generator.transform) translation = Vector3.zero;
                if (custom && part == location.m_interiorTransform) { translation = interior; orientation = Quaternion.Inverse(rotation); }
                matrix *= Matrix4x4.TRS(translation, orientation, part.localScale);
                worldRotation *= orientation;
            }
            return matrix;
        }
        private static Vector3 DoorPoint(Matrix4x4 matrix, Quaternion rotation) => (Vector3)matrix.GetColumn(3) + rotation * (Vector3.forward - Vector3.up);
        private static bool ReadRoute(ZDOID proxyId, int doorIndex, out Route route)
        {
            route = null;
            var proxy = ZDOMan.instance.GetZDO(proxyId);
            if (proxy == null || proxy.GetPrefab() != ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode()) return false;
            var location = LocationPrefab(ZoneSystem.instance, proxy.GetInt(ZDOVars.s_location));
            if (location == null) return false;
            location.m_prefab.Load();
            try
            {
                var prefab = location.m_prefab.Asset; var definition = prefab.GetComponent<Location>();
                var doors = prefab.GetComponentsInChildren<Teleport>(true);
                if (doorIndex < 0 || doorIndex >= doors.Length || !doors[doorIndex].m_targetPoint) return false;
                int target = Array.IndexOf(doors, doors[doorIndex].m_targetPoint);
                if (target < 0) return false;
                var from = DoorMatrix(doors[doorIndex].transform, prefab.transform, definition, proxy.GetPosition(), proxy.GetRotation(), out var fromRotation);
                var to = DoorMatrix(doors[target].transform, prefab.transform, definition, proxy.GetPosition(), proxy.GetRotation(), out var toRotation);
                var departure = DoorPoint(from, fromRotation); var destination = DoorPoint(to, toRotation);
                if (!Finite(departure) || !Finite(destination) || (departure.y > DungeonInteriors.InteriorHeight) == (destination.y > DungeonInteriors.InteriorHeight)) return false;
                var zone = ZoneSystem.GetZone(proxy.GetPosition());
                if (departure.y > DungeonInteriors.InteriorHeight && StarLevelSystem.Installed) DungeonInteriors.LocateInterior(zone, proxy, location);
                route = new Route { Proxy = proxyId, Door = doorIndex, Target = target, From = departure, To = destination,
                    Rotation = toRotation, Zone = zone };
                return true;
            }
            finally { location.m_prefab.Release(); }
        }

        private static bool Interact(Teleport __instance, Humanoid character, bool hold, ref bool __result)
        {
            if (replaying || hold || character != Player.m_localPlayer || !__instance.m_targetPoint || character.IsTeleporting()) return true;
            if (!character.IsOwner() || TeleportCooldown((Player)character) < 2f) return true;
            // Once the caster and every follower are through, the transfer only awaits its confirmation; using a doorway starts over.
            if (local != null && !Through(local)) { __result = false; return false; }
            CancelLocal();
            var player = (Player)character;
            if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBossPortals) && player.InInterior() && Location.IsInsideActiveBossDungeon(player.transform.position)) return true;
            var proxy = __instance.GetComponentInParent<LocationProxy>();
            var view = proxy ? proxy.GetComponent<ZNetView>() : null;
            if (!view || !view.IsValid()) return true;
            int index = Array.IndexOf(proxy.GetComponentsInChildren<Teleport>(true), __instance);
            if (!ReadRoute(view.GetZDO().m_uid, index, out var route)) return true;
            var selected = Character.GetAllCharacters().Where(creature => creature && !creature.IsDead() && !creature.HaveRider() &&
                !TameRiding.HoldingPosition(creature) && Follows(creature, player) &&
                (SummonedBats.SummonerId(creature) == player.GetPlayerID() || SummonOwnership.IsFollower(creature) &&
                    SummonOwnership.TryGet(creature, out long owner, out _) && owner == player.GetPlayerID()) &&
                SameSide(creature.transform.position, route) && SummonedBats.WithinSimulation(player, creature)).ToArray();
            if (selected.Length == 0) return true;
            local = new LocalTrip { Request = ++requestSequence, Actor = player.GetZDOID(), Door = __instance, Route = route, Expires = Time.realtimeSinceStartup + Lifetime };
            foreach (var creature in selected) local.Selected.Add(creature.GetZDOID());
            var packet = Packet(Request.Begin); packet.Write(local.Request); packet.Write(route.Proxy); packet.Write(route.Door); packet.Write(local.Selected.Count);
            foreach (var creature in selected)
            {
                var data = GameFields.CharacterView(creature).GetZDO();
                packet.Write(data.m_uid); packet.Write(data.DataRevision); ZDOMan.instance.ForceSendZDO(data.m_uid);
            }
            SendRequest(packet); __result = true; return false;
        }

        // A cave's outer doorway trigger surrounds its own arrival point, so arriving fires it again and the native
        // cooldown refuses that teleport. Only a teleport that actually starts elsewhere abandons the transfer.
        private static void TeleportAttempted(Player __instance, Vector3 pos, bool distantTeleport, bool __result)
        {
            if (__instance != Player.m_localPlayer || local == null) return;
            bool transfer = context == local.Token && local.Token != 0 && !distantTeleport && Vector3.Distance(pos, local.Route.To) <= .1f;
            if (!transfer) { if (__result) CancelLocal(); }
            else if (!__result) CancelLocal();
            else if (__instance.IsTeleporting()) local.Started = true;
        }
        internal static void CancelContext(long token) { if (local != null && local.Token == token) CancelLocal(); }

        private static ZPackage Packet(Request operation) { var packet = new ZPackage(); packet.Write((byte)operation); return packet; }
        private static ZPackage Packet(Delivery operation) { var packet = new ZPackage(); packet.Write((byte)operation); return packet; }
        private static void SendRequest(ZPackage packet)
        {
            if (ZNet.instance.IsServer()) { packet.SetPos(0); ReceiveRequest(null, packet); }
            else ZNet.instance.GetServerPeer()?.m_rpc.Invoke(RequestRpc, packet);
        }
        private static void Send(long owner, ZPackage packet)
        {
            if (owner == ZDOMan.GetSessionID()) { packet.SetPos(0); ReceiveDelivery(packet); }
            else ZNet.instance.GetPeer(owner)?.m_rpc.Invoke(DeliveryRpc, packet);
        }
        private static void Register(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer())
            {
                peer.m_rpc.Register<ZPackage>(RequestRpc, (rpc, packet) =>
                { if (rpc == peer.m_rpc && peer.IsReady() && rpc.IsConnected() && !peer.m_server) ReceiveRequest(peer, packet); });
                foreach (var trip in Trips.Values)
                    foreach (var target in trip.Targets.Where(value => value.Result == Result.Pending && value.PrepareSent != 0))
                    { var packet = Packet(Delivery.Prepare); WriteHeld(packet, trip, target); peer.m_rpc.Invoke(DeliveryRpc, packet); trip.Connections.Add(peer.m_rpc); }
            }
            else if (peer.m_server) peer.m_rpc.Register<ZPackage>(DeliveryRpc, (rpc, packet) =>
            { if (ZNet.instance.GetServerPeer()?.m_rpc == rpc) ReceiveDelivery(packet); });
        }

        private static void ReceiveRequest(ZNetPeer peer, ZPackage packet)
        {
            if (!ZNet.instance.IsServer() || packet.Size() > 65536) return;
            long sender = peer?.m_uid ?? ZDOMan.GetSessionID();
            try
            {
                var operation = (Request)packet.ReadByte();
                if (operation == Request.Begin)
                {
                    long request = packet.ReadLong();
                    if (request <= 0 || Trips.Values.Any(trip => trip.Peer == sender) || LastRequests.TryGetValue(sender, out long previous) && request <= previous) return;
                    var proxy = packet.ReadZDOID(); int door = packet.ReadInt(), count = packet.ReadInt();
                    if (count < 0 || count > packet.Size() / 16) return;
                    var revisions = new Dictionary<ZDOID, uint>();
                    for (int index = 0; index < count; index++) revisions[packet.ReadZDOID()] = packet.ReadUInt();
                    var actor = peer == null ? GameFields.CharacterView(Player.m_localPlayer)?.GetZDO() : ZDOMan.instance.GetZDO(peer.m_characterID);
                    if (!Alive(actor) || actor.GetLong(ZDOVars.s_playerID) == 0 || actor.GetOwner() != sender || !Finite(actor.GetPosition()) || !ReadRoute(proxy, door, out var route) || Vector3.Distance(actor.GetPosition(), route.From) > DoorRange) return;
                    LastRequests[sender] = request;
                    var trip = new Trip { Token = ++tokenSequence, Request = request, Actor = actor.m_uid, Peer = sender,
                        Player = actor.GetLong(ZDOVars.s_playerID), Name = peer?.m_playerName ?? Player.m_localPlayer.GetPlayerName(), Route = route,
                        Created = Time.realtimeSinceStartup, Expires = Time.realtimeSinceStartup + Lifetime, Lease = ZNet.instance.GetTime().AddSeconds(Lifetime).Ticks };
                    if (peer != null) trip.Connections.Add(peer.m_rpc);
                    foreach (var selected in revisions) trip.Targets.Add(new Target { Id = selected.Key, MinimumRevision = selected.Value });
                    Trips.Add(trip.Token, trip); return;
                }
                long token = packet.ReadLong();
                if (operation == Request.Cancel && token == 0)
                {
                    long request = packet.ReadLong();
                    var pending = Trips.Values.FirstOrDefault(trip => trip.Peer == sender && trip.Request == request);
                    if (pending != null) End(pending, false);
                    return;
                }
                if (!Trips.TryGetValue(token, out var current)) return;
                if (operation == Request.Arrived || operation == Request.Cancel)
                {
                    if (sender != current.Peer || peer != null && peer.m_characterID != current.Actor) return;
                    if (operation == Request.Cancel) End(current, false);
                    else current.ArrivalReported = true;
                    return;
                }
                var targetId = packet.ReadZDOID();
                var target = current.Targets.FirstOrDefault(value => value.Id == targetId);
                var data = target == null ? null : ZDOMan.instance.GetZDO(target.Id);
                if (data == null || data.GetOwner() != sender || !current.Recipients.Contains(sender)) return;
                if (operation == Request.Prepared)
                {
                    long expiry = packet.ReadLong();
                    bool bat = SummonedBats.SummonerId(data) != 0;
                    float lifetime = bat ? ZNetScene.instance.GetPrefab(data.GetPrefab()).GetComponent<TimedDestruction>().m_timeout : 0f;
                    if (expiry < 0 || bat && (expiry == 0 || expiry > ZNet.instance.GetTime().AddSeconds(Mathf.Max(0f, lifetime) + 2f).Ticks) || !bat && expiry != 0) return;
                    bool earlier = expiry > 0 && (target.TimerExpiry == 0 || expiry < target.TimerExpiry - TimeSpan.TicksPerMillisecond * 100);
                    if (earlier) { target.TimerExpiry = expiry; target.MoveSent = 0; target.Reported = Result.Pending; PrepareOwners(current, target); }
                    target.Prepared = sender;
                }
                else if (operation == Request.Rejected) target.Result = Result.Gone;
                else if (operation == Request.Moved)
                {
                    var result = (Result)packet.ReadByte();
                    var center = packet.ReadVector3(); float radius = packet.ReadSingle();
                    if (!Finite(center) || float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0f) return;
                    if (current.Arrived && target.MoveSent == sender && (result == Result.Moved || result == Result.Gone))
                    { target.Reported = result; if (result == Result.Gone) target.Result = result; target.Center = center; target.Radius = radius; }
                }
                else if (operation == Request.NeedGeometry && current.Arrived && target.MoveSent == sender)
                {
                    data.SetOwner(current.Peer); ZDOMan.instance.ForceSendZDO(target.Id); target.HandedOver = true; target.MoveSent = 0;
                }
            }
            catch (Exception error) when (error is EndOfStreamException || error is IOException || error is ArgumentException) { }
        }

        private static void WriteHeld(ZPackage packet, Trip trip, Target target, bool moving = false)
        {
            packet.Write(trip.Token); packet.Write(trip.Request); packet.Write(trip.Actor); packet.Write(trip.Player); packet.Write(trip.Name); packet.Write(trip.Lease); packet.Write(target.TimerExpiry);
            packet.Write(target.Id);
            if (moving) { packet.Write(trip.Route.Proxy); packet.Write(trip.Route.Target); packet.Write(trip.Route.To); packet.Write(trip.Route.Rotation); }
        }
        private static void PrepareOwners(Trip trip, Target target)
        {
            // A prospective owner must already know the distance-dismissal lease when native ownership changes.
            // Only identities and the short lease are broadcast; destination coordinates go to the moving owner.
            var localPacket = Packet(Delivery.Prepare); WriteHeld(localPacket, trip, target);
            trip.Recipients.Add(ZDOMan.GetSessionID()); Send(ZDOMan.GetSessionID(), localPacket);
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (!peer.m_rpc.IsConnected()) continue;
                var packet = Packet(Delivery.Prepare); WriteHeld(packet, trip, target); peer.m_rpc.Invoke(DeliveryRpc, packet);
                trip.Connections.Add(peer.m_rpc); if (peer.IsReady()) trip.Recipients.Add(peer.m_uid);
            }
        }
        private static void ReceiveDelivery(ZPackage packet)
        {
            if (packet.Size() > 65536 || ZNet.instance == null) return;
            try
            {
                var operation = (Delivery)packet.ReadByte(); long token = packet.ReadLong();
                if (operation == Delivery.Enter)
                {
                    long request = packet.ReadLong();
                    if (local == null || local.Request != request || !Player.m_localPlayer || Player.m_localPlayer.GetZDOID() != local.Actor) return;
                    local.Token = token; local.Entered = true;
                    if (!local.Door || Vector3.Distance(Player.m_localPlayer.transform.position, local.Route.From) > DoorRange) { CancelLocal(); return; }
                    replaying = true;
                    try { if (!InContext(token, () => local.Door.Interact(Player.m_localPlayer, false, false))) CancelLocal(); }
                    finally { replaying = false; }
                    return;
                }
                if (operation == Delivery.End)
                {
                    bool park = packet.ReadBool();
                    int count = packet.ReadInt(); if (count < 0 || count > packet.Size() / 12) return;
                    var arrived = new HashSet<ZDOID>(); for (int index = 0; index < count; index++) arrived.Add(packet.ReadZDOID());
                    if (Closed.Add(token)) Closing.Enqueue((token, Time.realtimeSinceStartup + Lifetime));
                    foreach (var held in HeldCreatures.Values.Where(value => value.Token == token).ToArray())
                    {
                        if (park && !held.Done && !arrived.Contains(held.Id)) Park(held);
                        HeldCreatures.Remove(held.Id);
                    }
                    if (local != null && local.Token == token)
                    {
                        // The RPC can precede the owner's ZDO update. Retain the replicas until their committed arrival is visible here.
                        local.Ended = true; local.Selected.IntersectWith(arrived);
                        local.Expires = Mathf.Max(local.Expires, Time.realtimeSinceStartup + 5f);
                    }
                    return;
                }
                long requestId = packet.ReadLong(); var actor = packet.ReadZDOID(); long player = packet.ReadLong(); string name = packet.ReadString(); long lease = packet.ReadLong(), timerExpiry = packet.ReadLong();
                var id = packet.ReadZDOID();
                if (lease <= 0 || Closed.Contains(token) || ZNetScene.instance != null && lease <= ZNet.instance.GetTime().Ticks) return;
                if (operation == Delivery.Prepare)
                {
                    if (local != null && local.Request == requestId && local.Actor == actor) local.Token = token;
                    if (!HeldCreatures.TryGetValue(id, out var existing) || existing.Token != token)
                        HeldCreatures[id] = new Held { Token = token, Actor = actor, Player = player, Name = name, Lease = lease, Id = id, TimerExpiry = timerExpiry };
                    else
                    {
                        existing.Prepared = existing.Move = existing.Done = existing.NeedsGeometry = false; existing.Occupied.Clear();
                        if (timerExpiry > 0 && (existing.TimerExpiry == 0 || timerExpiry < existing.TimerExpiry)) existing.TimerExpiry = timerExpiry;
                    }
                }
                else if (operation == Delivery.Move && HeldCreatures.TryGetValue(id, out var held) && held.Token == token && held.Actor == actor)
                {
                    var route = new Route { Proxy = packet.ReadZDOID(), Target = packet.ReadInt(), To = packet.ReadVector3(), Rotation = packet.ReadQuaternion() };
                    if (!Finite(route.To)) return;
                    int count = packet.ReadInt(); if (count < 0 || count > packet.Size() / 16) return;
                    held.Occupied.Clear();
                    for (int index = 0; index < count; index++) held.Occupied.Add((packet.ReadVector3(), packet.ReadSingle()));
                    held.Route = route; held.Move = true; held.Done = false;
                }
            }
            catch (Exception error) when (error is EndOfStreamException || error is IOException || error is ArgumentException) { }
        }

        private static string Completion(Held held) => (ZNet.instance.IsServer() ? ZDOMan.GetSessionID() : ZNet.instance.GetServerPeer()?.m_uid ?? 0L) + ":" + held.Token;
        private static string Completion(Trip trip) => ZDOMan.GetSessionID() + ":" + trip.Token;
        private static string Completion(LocalTrip trip) => (ZNet.instance.IsServer() ? ZDOMan.GetSessionID() : ZNet.instance.GetServerPeer()?.m_uid ?? 0L) + ":" + trip.Token;
        private static bool Through(LocalTrip trip) => trip.Started && trip.Token != 0 && trip.Selected.All(id =>
            !(ZDOMan.instance.GetZDO(id) is ZDO data) || !Alive(data) || data.GetString(MovedKey) == Completion(trip));
        private static void Reply(Request operation, Held held, Result result = Result.Pending, Vector3 center = default, float radius = 0f)
        {
            var packet = Packet(operation); packet.Write(held.Token); packet.Write(held.Id);
            if (operation == Request.Prepared) packet.Write(held.TimerExpiry);
            if (operation == Request.Moved) { packet.Write((byte)result); packet.Write(center); packet.Write(radius); }
            SendRequest(packet);
        }
        private static bool Following(Character creature, Held held)
        {
            if (!creature || !Belongs(GameFields.CharacterView(creature).GetZDO(), held.Player, held.Name)) return false;
            var following = creature.GetComponent<MonsterAI>()?.GetFollowTarget()?.GetComponent<Player>();
            return !following || following.GetPlayerID() == held.Player;
        }
        private static void Park(Held held)
        {
            var creature = ZNetScene.instance?.FindInstance(held.Id)?.GetComponent<Character>();
            if (!creature || !creature.IsOwner() || creature.IsDead() || !StaffFollower(GameFields.CharacterView(creature).GetZDO()) ||
                !Belongs(GameFields.CharacterView(creature).GetZDO(), held.Player, held.Name)) return;
            var ai = creature.GetComponent<MonsterAI>(); ai.SetFollowTarget(null); SetPatrol(ai);
            GameFields.CharacterView(creature).GetZDO().Set(ZDOVars.s_follow, "");
        }

        // Floor a person could walk to from the start within 10 m of the doorway's arrival point, nearest first, with
        // the direction of the last step there. Stairs and slopes count; walls, closed doors, cliffs and deep water end the search.
        private static IEnumerable<(Vector3 Floor, Vector3 Heading)> WalkableFloor(Vector3 start, Vector3 heading, Vector3 arrival)
        {
            var reached = new HashSet<Vector2Int> { Vector2Int.zero };
            var layer = new List<(Vector2Int Cell, Vector3 Floor, Vector3 Heading)> { (Vector2Int.zero, start, heading) };
            while (layer.Count > 0)
            {
                foreach (var entry in layer.OrderBy(value => Utils.DistanceXZ(value.Floor, start))) yield return (entry.Floor, entry.Heading);
                var next = new List<(Vector2Int Cell, Vector3 Floor, Vector3 Heading)>();
                foreach (var (cell, floor, _) in layer)
                    for (int x = -1; x <= 1; x++)
                        for (int z = -1; z <= 1; z++)
                        {
                            var neighbour = cell + new Vector2Int(x, z); var point = start + new Vector3(neighbour.x, 0f, neighbour.y) * Step;
                            if (reached.Contains(neighbour) || Utils.DistanceXZ(point, arrival) > SearchRadius || !WalkTo(floor, point, out var reachedFloor)) continue;
                            reached.Add(neighbour); next.Add((neighbour, reachedFloor, new Vector3(x, 0f, z)));
                        }
                layer = next;
            }
        }
        private static void Face(Character creature, Quaternion rotation)
        {
            if (Quaternion.Angle(creature.transform.rotation, rotation) < 1f) return;
            creature.transform.rotation = creature.GetComponent<Rigidbody>().rotation = rotation;
            Physics.SyncTransforms();
        }
        private static bool WalkTo(Vector3 from, Vector3 to, out Vector3 floor)
        {
            floor = to;
            // Up to 1.2 m up or 1.5 m down per metre covers stairs and ordinary slopes.
            if (!Physics.Raycast(new Vector3(to.x, from.y + 1.2f, to.z), Vector3.down, out var hit, 2.7f, FloorMask, QueryTriggerInteraction.Ignore) ||
                hit.normal.y < .5f || hit.point.y < ZoneSystem.instance.m_waterLevel - .5f) return false;
            floor = hit.point;
            return !Physics.Linecast(from + Vector3.up * .9f, floor + Vector3.up * .9f, SolidMask, QueryTriggerInteraction.Ignore) &&
                !Physics.Linecast(from + Vector3.up * 1.6f, floor + Vector3.up * 1.6f, SolidMask, QueryTriggerInteraction.Ignore) &&
                !Physics.CheckCapsule(floor + Vector3.up * .5f, floor + Vector3.up * 1.5f, .25f, SolidMask, QueryTriggerInteraction.Ignore);
        }

        private static bool Landing(Character creature, Held held, Vector3 center, out Vector3 position)
        {
            position = Vector3.zero;
            if (!ZoneSystem.instance.FindFloor(center, out float floor)) return false;
            var proxy = ZNetScene.instance.FindInstance(held.Route.Proxy);
            var doors = proxy ? proxy.GetComponentsInChildren<Teleport>(true) : Array.Empty<Teleport>();
            if (held.Route.Target >= doors.Length) return false;
            var doorway = doors[held.Route.Target]; var triggers = doorway.GetComponentsInChildren<Collider>(true).Where(value => value.enabled && value.isTrigger).ToArray();
            float offset = SummonedBats.IsBat(creature) ? 1f : .05f;
            var body = creature.GetComponent<Rigidbody>();
            Physics.SyncTransforms();
            // The nearest space the follower fits on floor its caster can walk to, counted from 2 m ahead of the doorway
            // so the way back stays clear. It faces the way it would have walked, so a long body fits a passage lengthwise.
            var origin = new Vector3(center.x, floor, center.z); var forward = held.Route.Rotation * Vector3.forward; forward.y = 0f;
            if (WalkTo(origin, center + forward * 2f, out var ahead)) origin = ahead;
            Vector3? nearest = null; var nearestFacing = creature.transform.rotation;
            foreach (var (candidate, heading) in WalkableFloor(origin, forward, center))
            {
                if (heading.sqrMagnitude > .01f) Face(creature, Quaternion.LookRotation(heading));
                if (!CreaturePlacement.Floor(creature, candidate, candidate.y, offset, out var point)) continue;
                var size = CreatureClearance.Measure(creature); var shift = point - creature.transform.position;
                if (triggers.Any(trigger => CreatureClearance.Colliders(creature).Any(shape => CreatureClearance.Obstructs(shape, body) &&
                    Physics.ComputePenetration(shape, shape.transform.position + shift, shape.transform.rotation, trigger, trigger.transform.position, trigger.transform.rotation, out _, out float depth) && depth > 0f))) continue;
                if (nearest == null) { nearest = point; nearestFacing = creature.transform.rotation; }
                if (!CreaturePlacement.Clear(creature, point, out _)) continue;
                if (held.Occupied.Any(other => Utils.DistanceXZ(other.Center, size.Bounds.center + shift) < Mathf.Max(.75f, other.Radius + size.Radius))) continue;
                // Clear has checked the actual bodies; this keeps a gap beside other creatures, including another
                // player's whose bodies trail their positions.
                if (Character.GetAllCharacters().Any(other => other && other != creature && !other.IsDead() &&
                    Vector3.Distance(other.GetCenterPoint(), size.Bounds.center + shift) < Mathf.Max(.75f, CreatureClearance.Measure(other).HalfWidth + size.HalfWidth))) continue;
                position = point; return true;
            }
            // Nothing in reach fits, as for a spirit animal larger than a crypt's passages: it still comes along to the
            // nearest floor it can stand on and may stay wedged there.
            Face(creature, nearestFacing);
            position = nearest ?? origin + Vector3.up * (offset - CreatureClearance.Measure(creature).Bottom);
            return true;
        }

        internal static void Tick()
        {
            if (ZNet.instance == null || ZDOMan.instance == null || ZNetScene.instance == null) return;
            while (Closing.Count > 0 && Closing.Peek().Until <= Time.realtimeSinceStartup) Closed.Remove(Closing.Dequeue().Token);
            if (local != null)
            {
                var player = Player.m_localPlayer;
                if (Time.realtimeSinceStartup > local.Expires && player && !player.IsDead() && !local.Started)
                    player.Message(MessageHud.MessageType.Center, "Could not enter the dungeon. Try again.");
                if (!player || player.IsDead() || player.GetZDOID() != local.Actor || Time.realtimeSinceStartup > local.Expires ||
                    !local.Entered && Vector3.Distance(player.transform.position, local.Route.From) > DoorRange ||
                    !ZNet.instance.IsServer() && ZNet.instance.GetServerPeer() == null) CancelLocal();
                else if (local.Ended)
                {
                    string completed = Completion(local);
                    if (local.Selected.All(id =>
                    {
                        var data = ZDOMan.instance.GetZDO(id); var instance = ZNetScene.instance.FindInstance(id)?.GetComponent<Character>();
                        return data != null && data.GetString(MovedKey) == completed && instance && Vector3.Distance(instance.transform.position, data.GetPosition()) < 1f;
                    })) local = null;
                }
                else if (local.Started && !player.IsTeleporting() && !local.Reported)
                {
                    if (Vector3.Distance(player.transform.position, local.Route.To) > ArrivalRange) CancelLocal();
                    else if (ZNetScene.instance.IsAreaReady(local.Route.To) && ZoneSystem.instance.FindFloor(local.Route.To, out _))
                    { local.Reported = true; var packet = Packet(Request.Arrived); packet.Write(local.Token); SendRequest(packet); }
                }
            }
            if (ZNet.instance.IsServer() && Time.realtimeSinceStartup >= nextServerTick)
            {
                nextServerTick = Time.realtimeSinceStartup + .1f;
                foreach (var trip in Trips.Values.ToArray())
                {
                    var actor = ZDOMan.instance.GetZDO(trip.Actor);
                    var peer = trip.Peer == ZDOMan.GetSessionID() ? null : ZNet.instance.GetPeer(trip.Peer);
                    if (!Alive(actor) || actor.GetOwner() != trip.Peer || peer != null && (!peer.IsReady() || peer.m_characterID != trip.Actor) ||
                        peer == null && trip.Peer != ZDOMan.GetSessionID() || Time.realtimeSinceStartup > trip.Expires)
                    { End(trip, trip.Arrived); continue; }
                    if (!trip.Entered && Vector3.Distance(actor.GetPosition(), trip.Route.From) > DoorRange) { End(trip, false); continue; }
                    if (trip.ArrivalReported && Vector3.Distance(actor.GetPosition(), trip.Route.To) <= ArrivalRange) trip.Arrived = true;
                    foreach (var target in trip.Targets.Where(value => value.Result == Result.Pending))
                    {
                        var data = ZDOMan.instance.GetZDO(target.Id);
                        if (data == null && Time.realtimeSinceStartup - trip.Created < 2f) continue;
                        // A reliable interaction RPC can overtake the native ZDO packet containing its new Follow order.
                        if (data != null && data.DataRevision < target.MinimumRevision) continue;
                        if (!Alive(data)) { target.Result = Result.Gone; continue; }
                        if (target.TimerExpiry > 0 && target.TimerExpiry <= ZNet.instance.GetTime().Ticks) { target.Result = Result.Gone; continue; }
                        string completed = data.GetString(MovedKey);
                        bool moved = trip.Arrived && completed == Completion(trip);
                        if (moved && target.Reported == Result.Moved) { target.Result = Result.Moved; continue; }
                        if (!moved && (!Belongs(data, trip.Player, trip.Name) || !trip.Entered && (!SameSide(data.GetPosition(), trip.Route) || !CreatureOwnership.CanOwn(data, trip.Peer))))
                        { target.Result = Result.Gone; continue; }
                        long owner = data.GetOwner();
                        var controller = owner == ZDOMan.GetSessionID() ? null : ZNet.instance.GetPeer(owner);
                        if (owner == 0 || owner != ZDOMan.GetSessionID() && (controller == null || !controller.IsReady()))
                        { data.SetOwner(trip.Peer); ZDOMan.instance.ForceSendZDO(target.Id); owner = trip.Peer; target.HandedOver = trip.Arrived; }
                        if (target.PrepareSent != owner || target.PrepareRevision != data.OwnerRevision)
                        {
                            target.PrepareSent = owner; target.PrepareRevision = data.OwnerRevision; target.Prepared = 0; target.MoveSent = 0; target.Reported = Result.Pending; trip.Recipients.Add(owner);
                            PrepareOwners(trip, target);
                        }
                    }
                    if (!trip.Entered && trip.Targets.All(target => target.Result != Result.Pending || target.Prepared != 0))
                    { trip.Entered = true; var packet = Packet(Delivery.Enter); packet.Write(trip.Token); packet.Write(trip.Request); Send(trip.Peer, packet); }
                    var moving = trip.Arrived ? trip.Targets.FirstOrDefault(target => target.Result == Result.Pending) : null;
                    if (moving != null && moving.Prepared != 0 && moving.MoveSent != moving.Prepared)
                    {
                        moving.MoveSent = moving.Prepared; var packet = Packet(Delivery.Move); WriteHeld(packet, trip, moving, true);
                        var occupied = trip.Targets.Where(target => target.Result == Result.Moved).ToArray(); packet.Write(occupied.Length);
                        foreach (var target in occupied) { packet.Write(target.Center); packet.Write(target.Radius); }
                        Send(moving.MoveSent, packet);
                    }
                    if (trip.Arrived && trip.Targets.All(target => target.Result != Result.Pending)) End(trip, true);
                }
            }
            foreach (var held in HeldCreatures.Values.ToArray())
            {
                var data = ZDOMan.instance.GetZDO(held.Id);
                if (held.Lease <= ZNet.instance.GetTime().Ticks || data != null && !Alive(data) || !ZNet.instance.IsServer() && ZNet.instance.GetServerPeer() == null)
                { HeldCreatures.Remove(held.Id); continue; }
                if (data == null) continue;
                var creature = ZNetScene.instance.FindInstance(held.Id)?.GetComponent<Character>();
                var timer = creature && SummonedBats.IsBat(creature) ? creature.GetComponent<TimedDestruction>() : null;
                if (timer && held.TimerExpiry > 0) ApplyBatTimer(timer, held.TimerExpiry);
                if (data.GetOwner() != ZDOMan.GetSessionID()) continue;
                if (SummonedBats.SummonerId(data) != 0)
                {
                    if (!timer || !BatTimers.TryGetValue(timer, out var scheduled)) continue;
                    if (held.TimerExpiry == 0) held.TimerExpiry = ZNet.instance.GetTime().AddSeconds(Mathf.Max(0f, scheduled.Deadline - Time.time)).Ticks;
                    data.Set(BatExpiryKey, held.TimerExpiry);
                    if (held.TimerExpiry <= ZNet.instance.GetTime().Ticks)
                    { if (!held.Done) Reply(Request.Rejected, held); held.Done = true; continue; }
                }
                bool completed = data.GetString(MovedKey) == Completion(held);
                if (!held.Prepared)
                {
                    if (!completed && creature && !Following(creature, held)) { held.Done = true; held.Prepared = true; Reply(Request.Rejected, held); continue; }
                    held.Prepared = true; Reply(Request.Prepared, held);
                }
                if (!held.Move || held.Done) continue;
                var actor = ZDOMan.instance.GetZDO(held.Actor);
                if (!Alive(actor) || Vector3.Distance(actor.GetPosition(), held.Route.To) > SimulationPresets.Radius(ZNet.instance.GetSyncedSimulationDistance())) continue;
                if (!ZNetScene.instance.IsAreaReady(actor.GetPosition()))
                { if (!held.NeedsGeometry) { held.NeedsGeometry = true; Reply(Request.NeedGeometry, held); } continue; }
                var player = ZNetScene.instance.FindInstance(held.Actor)?.GetComponent<Player>();
                if (!creature || !player || FreshCreatureSpawns.Waiting(creature)) continue;
                if (Vector3.Distance(player.transform.position, actor.GetPosition()) > 1f) continue;
                if (completed)
                {
                    held.Done = true; var completedSize = CreatureClearance.Measure(creature);
                    Reply(Request.Moved, held, Result.Moved, completedSize.Bounds.center + data.GetPosition() - creature.transform.position, completedSize.Radius); continue;
                }
                if (!Following(creature, held)) { held.Done = true; Reply(Request.Moved, held, Result.Gone); continue; }
                if (!Landing(creature, held, actor.GetPosition(), out var position))
                { if (!held.NeedsGeometry) { held.NeedsGeometry = true; Reply(Request.NeedGeometry, held); } continue; }
                CreaturePlacement.Move(creature, position);
                var ai = creature.GetComponent<MonsterAI>(); Path(ai).Clear(); LastPath(ai) = float.NegativeInfinity; ResetPatrol(ai); CombatTarget(ai) = null;
                if (SummonedBats.IsBat(creature)) SummonedBats.ResetAfterDungeon(creature, player);
                else SummonedFollowers.ResetAfterDungeon(creature, player);
                data.Set(MovedKey, Completion(held));
                data.Set(SettlingKey, ZNet.instance.GetTime().AddSeconds(5f).Ticks);
                ZDOMan.instance.ForceSendZDO(held.Id); held.Done = true; var movedSize = CreatureClearance.Measure(creature);
                Reply(Request.Moved, held, Result.Moved, movedSize.Bounds.center, movedSize.Radius);
            }
        }

        // Every tame runs this every frame; Tameable keeps the view its Awake found.
        private static bool KeepSelectedSummon(Tameable __instance, ZNetView ___m_nview)
        {
            var data = ___m_nview?.GetZDO();
            if (data == null || !StaffFollower(data)) return true;
            if (HeldCreatures.TryGetValue(data.m_uid, out var held) && held.Lease > ZNet.instance.GetTime().Ticks && Belongs(data, held.Player, held.Name)) return false;
            // Position packets and the remote Player's visual transform need not update in the same frame.
            // A completed transfer must not dismiss an actually nearby follower because its caster's replica still lags behind.
            if (!data.IsOwner() || data.GetLong(SettlingKey) <= ZNet.instance.GetTime().Ticks || __instance.m_unsummonDistance <= 0f) return true;
            var following = __instance.GetComponent<MonsterAI>()?.GetFollowTarget()?.GetComponent<Player>();
            var actor = following ? GameFields.CharacterView(following)?.GetZDO() : null;
            return actor == null || !actor.HasOwner() || !SummonOwnership.Recorded(data, following.GetPlayerID(), SummonOwnership.StaffFor(data)) ||
                Vector3.Distance(actor.GetPosition(), __instance.transform.position) > __instance.m_unsummonDistance;
        }
        private static void KeepReplicas(List<ZDO> currentNearObjects)
        {
            var identities = HeldCreatures.Values.Where(value => value.Prepared || value.Move).Select(value => value.Id)
                .Concat(HeldCreatures.Values.Where(value => value.Move && value.Route != null).Select(value => value.Route.Proxy))
                .Concat(local?.Selected ?? Enumerable.Empty<ZDOID>());
            if (local != null) identities = identities.Concat(new[] { local.Route.Proxy });
            foreach (var id in identities.Distinct())
            { var data = ZDOMan.instance.GetZDO(id); if (data != null && !currentNearObjects.Contains(data)) currentNearObjects.Add(data); }
        }
        private static void KeepTransferOwner(ZDO creature, long owner, ref bool __result)
        {
            if (__result) return;
            if (ZNet.instance.IsServer()) __result = Trips.Values.Any(trip => trip.Arrived && trip.Peer == owner && Time.realtimeSinceStartup <= trip.Expires &&
                trip.Targets.Any(target => target.Id == creature.m_uid && target.HandedOver && target.Result == Result.Pending));
            else __result = owner == ZDOMan.GetSessionID() && HeldCreatures.TryGetValue(creature.m_uid, out var held) && held.Move && !held.Done && held.Lease > ZNet.instance.GetTime().Ticks;
        }
        private static void End(Trip trip, bool park)
        {
            if (!Trips.Remove(trip.Token)) return;
            var packet = Packet(Delivery.End); packet.Write(trip.Token); packet.Write(park);
            var moved = trip.Targets.Where(target => target.Result == Result.Moved).ToArray(); packet.Write(moved.Length);
            foreach (var target in moved) packet.Write(target.Id);
            Send(ZDOMan.GetSessionID(), new ZPackage(packet.GetArray()));
            foreach (var connection in trip.Connections) if (connection.IsConnected()) connection.Invoke(DeliveryRpc, packet);
        }
        private static void CancelLocal()
        {
            if (local == null) return;
            long token = local.Token, request = local.Request; local = null;
            if (ZNet.instance != null) { var packet = Packet(Request.Cancel); packet.Write(token); packet.Write(request); SendRequest(packet); }
        }
        private static void Disconnecting(ZNetPeer peer)
        {
            if (!ZNet.instance.IsServer()) { if (peer.m_server) Clear(); return; }
            foreach (var trip in Trips.Values.Where(value => value.Peer == peer.m_uid).ToArray()) End(trip, false);
            LastRequests.Remove(peer.m_uid);
        }
        private static void Clear()
        { Trips.Clear(); LastRequests.Clear(); HeldCreatures.Clear(); Closed.Clear(); Closing.Clear(); local = null; context = 0; replaying = false; nextServerTick = 0f; requestSequence = tokenSequence = 0; }
    }
}
