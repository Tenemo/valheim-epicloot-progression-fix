using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Whoever steers a ship simulates it. Only a ship's owner runs its physics, and the game never moves ownership to
    // the helmsman: ShipControlls.RPC_RequestControl only records the user, so every rudder and speed input made a
    // round trip through another game. The owner now hands the ship over when the helm is taken, the owner and the
    // server give it back at once if anything else takes it while someone steers, and ship storage use never moves it.
    // With nobody steering, the native owner rules apply.
    internal static class HelmSimulation
    {
        private const float PendingSeconds = 2f, ReportSeconds = 5f;
        private static readonly AccessTools.FieldRef<Ship, ZNetView> ShipView = AccessTools.FieldRefAccess<Ship, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Ship, Ship.Speed> Speed = AccessTools.FieldRefAccess<Ship, Ship.Speed>("m_speed");
        private static readonly AccessTools.FieldRef<Ship, List<Player>> Players = AccessTools.FieldRefAccess<Ship, List<Player>>("m_players");
        private static readonly AccessTools.FieldRef<ZSyncTransform, bool> WasOwner = AccessTools.FieldRefAccess<ZSyncTransform, bool>("m_wasOwner");
        private static readonly AccessTools.FieldRef<Player, IDoodadController> Controlled =
            AccessTools.FieldRefAccess<Player, IDoodadController>("m_doodadController");
        private static readonly AccessTools.FieldRef<Container, bool> InUse = AccessTools.FieldRefAccess<Container, bool>("m_inUse");
        private static readonly AccessTools.FieldRef<InventoryGui, Container> OpenContainer = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
        private static readonly Func<Container, bool> LoadContainer = AccessTools.MethodDelegate<Func<Container, bool>>(AccessTools.Method(typeof(Container), "Load"));
        private static readonly Dictionary<int, bool> ShipPrefabs = new Dictionary<int, bool>();
        private static readonly Dictionary<ZDOID, (float Time, int Skipped)> Reports = new Dictionary<ZDOID, (float, int)>();
        private static (Ship Ship, Ship.Speed Speed, float Time) pending;
        private static ManualLogSource log;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource logger)
        {
            log = logger;
            var type = typeof(HelmSimulation);
            harmony.Patch(AccessTools.Method(typeof(ShipControlls), "RPC_RequestControl"), postfix: new HarmonyMethod(type, nameof(Granted)));
            harmony.Patch(AccessTools.Method(typeof(Ship), nameof(Ship.CustomFixedUpdate)), prefix: new HarmonyMethod(type, nameof(KeepWithHelmsman)));
            harmony.Patch(AccessTools.Method(typeof(Ship), nameof(Ship.Forward)), prefix: new HarmonyMethod(type, nameof(BeforeForward)));
            harmony.Patch(AccessTools.Method(typeof(Ship), nameof(Ship.Backward)), prefix: new HarmonyMethod(type, nameof(BeforeBackward)));
            harmony.Patch(AccessTools.Method(typeof(ZDOMan), "RPC_ZDOData"), postfix: new HarmonyMethod(type, nameof(Received)));
            // Network Performance System sends the peers on its own schedule from this method and skips the game's loop,
            // which would skip every prefix after it, so this one runs first.
            harmony.Patch(AccessTools.Method(typeof(ZDOMan), "SendZDOToPeers2"),
                prefix: new HarmonyMethod(type, nameof(Guard)) { priority = Priority.First });
            // MultiUserChest answers these requests itself (StorageRequests below); this covers its absence.
            var keep = new HarmonyMethod(type, nameof(KeepStorage)) { after = new[] { PluginIds.MultiUserChest } };
            harmony.Patch(AccessTools.Method(typeof(Container), "RPC_RequestOpen"), prefix: keep);
            harmony.Patch(AccessTools.Method(typeof(Container), "RPC_RequestStack"), prefix: keep);
        }

        private static Player Helmsman(Ship ship)
        {
            long user = ship.m_shipControlls ? ship.m_shipControlls.GetUser() : 0L;
            if (user != 0L)
                foreach (var player in Players(ship))
                    if (player && player.GetPlayerID() == user) return player;
            return null;
        }

        // Runs on the ship's owner, after the native grant has recorded the helmsman.
        private static void Granted(ShipControlls __instance, long sender, long playerID)
        {
            var ship = __instance.m_ship;
            if (ship.IsOwner() && __instance.GetUser() == playerID && sender != ZDOMan.GetSessionID()) HandOver(ship, sender);
        }

        private static void KeepWithHelmsman(Ship __instance)
        {
            if (!__instance.IsOwner()) return;
            var helmsman = Helmsman(__instance);
            long game = helmsman ? helmsman.GetOwner() : 0L;
            if (game == 0L || game == ZDOMan.GetSessionID()) return;
            Report(ShipView(__instance).GetZDO(), $"Handed {Utils.GetPrefabName(__instance.gameObject)} to its helmsman {helmsman.GetPlayerName()}; this game was still simulating it.");
            HandOver(__instance, game);
        }

        // The helmsman's game continues from the latest speed, rudder, pose and velocities.
        private static void HandOver(Ship ship, long owner)
        {
            var data = ShipView(ship).GetZDO();
            data.Set(ZDOVars.s_forward, (int)ship.GetSpeedSetting());
            data.Set(ZDOVars.s_rudder, ship.GetRudderValue());
            var sync = ship.GetComponent<ZSyncTransform>();
            if (sync) sync.SyncNow();
            data.SetOwner(owner);
            ZDOMan.instance.ForceSendZDO(owner, data.m_uid);
            Lost(ship);
        }

        // Ownership arrives with the ship's replicated data. Act on each change before the transform sync does.
        private static void Received()
        {
            foreach (var instance in Ship.Instances)
            {
                var ship = (Ship)instance;
                var sync = ship.GetComponent<ZSyncTransform>();
                if (!sync || WasOwner(sync) == ship.IsOwner()) continue;
                if (ship.IsOwner()) TookOver(ship, sync);
                else Lost(ship);
            }
        }

        private static void TookOver(Ship ship, ZSyncTransform sync)
        {
            var data = ShipView(ship).GetZDO();
            // The transform sync snaps a new owner to the last received pose, which lags behind the ship this game
            // already shows by the network delay. Keep the shown pose; the synced body velocity still carries over.
            data.SetPosition(ship.transform.position);
            data.SetRotation(ship.transform.rotation);
            var body = ship.GetComponent<Rigidbody>();
            if (body && !sync.m_syncBodyVelocity) body.linearVelocity = data.GetVec3(ZDOVars.s_velHash, Vector3.zero);
            var speed = (Ship.Speed)data.GetInt(ZDOVars.s_forward);
            if (pending.Ship == ship && Time.time - pending.Time < PendingSeconds) speed = pending.Speed;
            pending = default;
            Speed(ship) = speed;
            data.Set(ZDOVars.s_forward, (int)speed);
            var hold = ship.GetComponentInChildren<Container>();
            if (!hold) return;
            var inventory = InventoryGui.instance;
            bool open = inventory && InventoryGui.IsVisible() && OpenContainer(inventory) == hold;
            InUse(hold) = false;
            LoadContainer(hold);
            InUse(hold) = open;
        }

        private static void Lost(Ship ship)
        {
            if (pending.Ship == ship) pending = default;
            // Only an owner can clear its storage's in-use mark, which would otherwise keep blocking this game's reloads.
            var hold = ship.GetComponentInChildren<Container>();
            if (hold) InUse(hold) = false;
        }

        private static void BeforeForward(Ship __instance) => Step(__instance, true);
        private static void BeforeBackward(Ship __instance) => Step(__instance, false);

        // A speed step sent while the ownership is on its way here reaches a previous owner that has let go and is
        // lost. Remember the speed it selects and apply it once this game owns the ship.
        private static void Step(Ship ship, bool faster)
        {
            var player = Player.m_localPlayer;
            if (ship.IsOwner() || !player || !ReferenceEquals(Controlled(player), ship.m_shipControlls)) return;
            var speed = pending.Ship == ship && Time.time - pending.Time < PendingSeconds ? pending.Speed : ship.GetSpeedSetting();
            pending = (ship, faster ? Faster(speed) : Slower(speed), Time.time);
        }

        // The native Forward and Backward steps.
        private static Ship.Speed Faster(Ship.Speed speed) =>
            speed == Ship.Speed.Stop ? Ship.Speed.Slow : speed == Ship.Speed.Slow ? Ship.Speed.Half : speed == Ship.Speed.Back ? Ship.Speed.Stop : Ship.Speed.Full;
        private static Ship.Speed Slower(Ship.Speed speed) =>
            speed == Ship.Speed.Full ? Ship.Speed.Half : speed == Ship.Speed.Half ? Ship.Speed.Slow : speed == Ship.Speed.Slow ? Ship.Speed.Stop : Ship.Speed.Back;

        // Before each of its send passes, after the frame's received data and native releases, the server returns a
        // steered ship to its helmsman's game, so another owner is never sent out. It reads only replicated records:
        // each connected character's ship, that ship's recorded helmsman and its owner.
        private static void Guard()
        {
            if (!ZNet.instance || !ZNet.instance.IsServer()) return;
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer.IsReady()) Guard(peer.m_uid, peer.m_characterID, peer.m_playerName);
            var host = Player.m_localPlayer;
            if (host) Guard(ZDOMan.GetSessionID(), host.GetZDOID(), host.GetPlayerName());
        }

        private static void Guard(long game, ZDOID character, string name)
        {
            var body = character.IsNone() ? null : ZDOMan.instance.GetZDO(character);
            if (body == null) return;
            var id = body.GetConnectionZDOID(ZDOExtraData.ConnectionType.SyncTransform);
            var ship = id.IsNone() ? null : ZDOMan.instance.GetZDO(id);
            if (ship == null || ship.GetOwner() == game) return;
            long user = ship.GetLong(ZDOVars.s_user);
            if (user == 0L || user != body.GetLong(ZDOVars.s_playerID) || !IsShip(ship)) return;
            long previous = ship.GetOwner();
            ship.SetOwner(game);
            ZDOMan.instance.ForceSendZDO(game, id);
            Report(ship, $"Gave {PrefabName(ship)} back to its helmsman {name}; {Describe(previous)} had taken it.");
        }

        private static bool IsShip(ZDO data)
        {
            int prefab = data.GetPrefab();
            if (ShipPrefabs.TryGetValue(prefab, out bool ship)) return ship;
            var scene = ZNetScene.instance;
            if (!scene) return false;
            var found = scene.GetPrefab(prefab);
            return ShipPrefabs[prefab] = found && found.GetComponent<Ship>();
        }

        private static string PrefabName(ZDO data)
        {
            var found = ZNetScene.instance ? ZNetScene.instance.GetPrefab(data.GetPrefab()) : null;
            return found ? found.name : "a ship";
        }

        private static string Describe(long game)
        {
            if (game == 0L) return "nobody";
            if (game == ZDOMan.GetSessionID()) return "the server";
            var peer = ZNet.instance.GetPeer(game);
            return peer != null ? peer.m_playerName : "game " + game;
        }

        // Each correction is logged; repeats within a few seconds are counted into the next report.
        private static void Report(ZDO ship, string message)
        {
            if (Reports.TryGetValue(ship.m_uid, out var last) && Time.time - last.Time < ReportSeconds)
            {
                Reports[ship.m_uid] = (last.Time, last.Skipped + 1);
                return;
            }
            Reports[ship.m_uid] = (Time.time, 0);
            log.LogInfo(last.Skipped > 0 ? $"{message} {last.Skipped} more corrections followed the previous report." : message);
        }

        private static bool ShipStorage(Container container)
        {
            var view = container ? GameFields.ContainerView(container) : null;
            return view && view.IsValid() && view.GetComponent<Ship>();
        }

        // Natively, opening or stacking into a chest makes the requester its owner, and for ship storage that owner
        // simulates the ship. Refuse other games' requests instead of handing the ship over.
        private static bool KeepStorage(Container __instance, long uid, bool __runOriginal, MethodBase __originalMethod)
        {
            if (!__runOriginal || uid == ZNet.GetUID() || !ShipStorage(__instance)) return true;
            var view = GameFields.ContainerView(__instance);
            if (!view.IsOwner()) return true;
            view.InvokeRPC(uid, __originalMethod.Name == "RPC_RequestOpen" ? "RPC_OpenResponse" : "RPC_StackResponse", false);
            return false;
        }

        // MultiUserChest makes whoever opens or stacks into a chest its owner unless someone else has it open, and
        // otherwise grants shared use with confirmed transfers. Ship storage always takes the shared path for other games.
        [HarmonyPatch]
        private static class StorageRequests
        {
            private static MethodInfo inUse;
            private static Func<Container, long, bool> sharedUse;
            private static bool Prepare() => PluginIds.TryGet(PluginIds.MultiUserChest, out _);
            private static IEnumerable<MethodBase> TargetMethods()
            {
                PluginIds.TryGet(PluginIds.MultiUserChest, out var plugin);
                var patches = plugin.Instance.GetType().Assembly.GetType("MultiUserChest.Patches.ContainerPatch", true);
                inUse = AccessTools.Method(patches, "IsContainerInUse", new[] { typeof(Container), typeof(long) })
                    ?? throw new MissingMethodException("MultiUserChest.Patches.ContainerPatch", "IsContainerInUse");
                sharedUse = AccessTools.MethodDelegate<Func<Container, long, bool>>(inUse);
                yield return AccessTools.Method(patches, "ContainerRPC_RequestOpenPatch");
                yield return AccessTools.Method(patches, "ContainerRPC_RequestStackPatch");
            }
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.Calls(inUse), AccessTools.Method(typeof(StorageRequests), nameof(SharedUse)), 1,
                    "MultiUserChest's chest request decision changed.");
            private static bool SharedUse(Container container, long uid) => sharedUse(container, uid) || uid != ZNet.GetUID() && ShipStorage(container);
        }

        // AzuCraftyBoxes edits a nearby chest's inventory in place. Changes to ship storage another game simulates
        // would be overwritten by that game's next update, so crafting, building and station supply leave it out.
        [HarmonyPatch]
        private static class StorageSupply
        {
            private static MethodInfo openElsewhere;
            private static Func<Container, bool> usedElsewhere;
            private static bool Prepare() => PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out _);
            private static MethodBase TargetMethod()
            {
                PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var plugin);
                var boxes = plugin.Instance.GetType().Assembly.GetType("AzuCraftyBoxes.Util.Functions.Boxes", true);
                openElsewhere = AccessTools.Method(boxes, "OpenedBySomeoneElse", new[] { typeof(Container) })
                    ?? throw new MissingMethodException("AzuCraftyBoxes.Util.Functions.Boxes", "OpenedBySomeoneElse");
                usedElsewhere = AccessTools.MethodDelegate<Func<Container, bool>>(openElsewhere);
                return AccessTools.Method(boxes, "GetNearbyContainers").MakeGenericMethod(typeof(Component));
            }
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.Calls(openElsewhere), AccessTools.Method(typeof(StorageSupply), nameof(UsedElsewhere)), 1,
                    "AzuCraftyBoxes' nearby chest selection changed.");
            private static bool UsedElsewhere(Container container) =>
                usedElsewhere(container) || ShipStorage(container) && !GameFields.ContainerView(container).IsOwner();
        }
    }
}
