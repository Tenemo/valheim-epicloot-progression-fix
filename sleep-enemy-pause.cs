using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static partial class SleepEnemyPause
    {
        private const string StateMessage = Plugin.Id + ".sleep-state";
        private const string ReadyMessage = Plugin.Id + ".sleep-ready";
        private const int Sleeping = 1, Waking = 2, Released = 3;
        private const float ReleaseLimit = 120f;
        private static readonly HashSet<ZRpc> Waiting = new();
        private static readonly Action<Game, long> NativeStart = AccessTools.MethodDelegate<Action<Game, long>>(AccessTools.Method(typeof(Game), "SleepStart"));
        private static readonly Action<Game, long> NativeStop = AccessTools.MethodDelegate<Action<Game, long>>(AccessTools.Method(typeof(Game), "SleepStop"));
        private static long nextCycle = DateTime.UtcNow.Ticks, cycle, worldStarted, transitionCycle;
        private static int phase, transitionPhase, renderedFrame = -1;
        private static bool stateKnown, participant, localWaiting, readySent, checkingFrame, nativeTransition;
        private static float noticeAt, releaseAt;
        private static MonoBehaviour runner;
        private static ManualLogSource log;

        internal static bool Active => phase == Sleeping || phase == Waking;
        internal static bool Paused => Active || !stateKnown && ZNet.instance && !ZNet.instance.IsServer() && ZNet.instance.GetServerPeer() != null;

        internal static void Initialize(IntegrationPatches patches, ManualLogSource logger, MonoBehaviour host)
        {
            log = logger; runner = host;
            patches.Patch(AccessTools.Method(typeof(Game), "UpdateSleeping"),
                prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowSleepPoll)),
                transpiler: new HarmonyMethod(typeof(SleepEnemyPause), nameof(SleepTransitions)));
            foreach (string method in new[] { "SleepStart", "SleepStop" })
                patches.Patch(AccessTools.Method(typeof(Game), method), prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AuthorizedTransition)));
            patches.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(RegisterPeer)));
            patches.Patch(AccessTools.Method(typeof(ZNet), "RPC_PeerInfo"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(PeerAuthenticated)));
            patches.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.Disconnect)), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(Disconnected)));
            patches.Patch(AccessTools.Method(typeof(ZNet), "Update"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(WatchReadiness)));
            patches.Patch(AccessTools.Method(typeof(Hud), "LateUpdate"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AfterFade)));
            patches.Patch(AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.Shutdown)), prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(LeaveWorld)));
            patches.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.Shutdown)), prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(LeaveWorld)));
            IntegrationChanges.Defer(() => Camera.onPostRender += Rendered, () => Camera.onPostRender -= Rendered);
            InitializeSimulation(patches);
            InitializeSpawning(patches);
        }

        private static bool AllowSleepPoll() => phase != Waking;
        private static bool AuthorizedTransition() => nativeTransition;

        private static IEnumerable<CodeInstruction> SleepTransitions(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var skip = AccessTools.Method(typeof(EnvMan), nameof(EnvMan.SkipToMorning));
            var broadcast = AccessTools.Method(typeof(ZRoutedRpc), nameof(ZRoutedRpc.InvokeRoutedRPC), new[] { typeof(long), typeof(string), typeof(object[]) });
            var skips = code.Where(instruction => instruction.Calls(skip)).ToArray();
            var broadcasts = code.Where(instruction => instruction.Calls(broadcast)).ToArray();
            if (skips.Length != 1 || broadcasts.Length != 2)
                throw new InvalidOperationException("The native global sleep transitions changed.");
            skips[0].opcode = OpCodes.Call;
            skips[0].operand = AccessTools.Method(typeof(SleepEnemyPause), nameof(BeginSkip));
            foreach (var instruction in broadcasts)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(SleepEnemyPause), nameof(BroadcastTransition));
            }
            return code;
        }

        private static void BeginSkip(EnvMan environment)
        {
            cycle = ++nextCycle; phase = Sleeping; worldStarted = ZNet.instance.GetTime().Ticks;
            participant = localWaiting = Player.m_localPlayer;
            readySent = checkingFrame = false; renderedFrame = -1; noticeAt = Time.unscaledTime + 60f;
            Waiting.Clear();
            foreach (var peer in ZNet.instance.GetPeers()) if (peer.IsReady()) Waiting.Add(peer.m_rpc);
            SuspendSpawning(); SuspendSimulation();
            foreach (var peer in ZNet.instance.GetPeers()) if (peer.IsReady()) SendState(peer, 0);
            environment.SkipToMorning();
        }

        private static void BroadcastTransition(ZRoutedRpc network, long target, string method, object[] arguments)
        {
            if (method == "SleepStart")
            {
                ApplyTransition(1, ZNet.GetUID());
                foreach (var peer in ZNet.instance.GetPeers()) if (Waiting.Contains(peer.m_rpc)) SendState(peer, 1);
            }
            else if (method == "SleepStop")
            {
                phase = Waking; releaseAt = Time.unscaledTime + ReleaseLimit;
                ApplyTransition(2, ZNet.GetUID());
                foreach (var peer in ZNet.instance.GetPeers()) if (peer.IsReady()) SendState(peer, 2);
                TryRelease();
            }
            else throw new InvalidOperationException("Unexpected native sleep transition: " + method);
        }

        private static void RegisterPeer(ZNet __instance, ZNetPeer peer)
        {
            peer.m_rpc.Register<ZPackage>(StateMessage, ReceiveState);
            peer.m_rpc.Register<long, int>(ReadyMessage, ReceiveReady);
            if (!__instance.IsServer()) stateKnown = false;
        }

        private static void PeerAuthenticated(ZRpc rpc)
        {
            if (!ZNet.instance.IsServer()) return;
            var peer = ZNet.instance.GetPeers().FirstOrDefault(value => ReferenceEquals(value.m_rpc, rpc));
            if (peer != null && peer.IsReady() && rpc.IsConnected()) SendState(peer, 0);
        }

        private static void SendState(ZNetPeer peer, int transition)
        {
            var message = new ZPackage();
            message.Write(cycle); message.Write(phase); message.Write(transition);
            message.Write(worldStarted); message.Write(ZNet.instance.GetTime().Ticks);
            peer.m_rpc.Invoke(StateMessage, message);
        }

        private static void ReceiveState(ZRpc connection, ZPackage message)
        {
            if (!ZNet.instance || ZNet.instance.IsServer()) return;
            // Gameplay login can still be connecting when this snapshot arrives. The designated server
            // socket is already known; GetServerPeer deliberately hides it while Connecting.
            var server = ZNet.instance.GetPeers().FirstOrDefault(peer => peer.m_server && ReferenceEquals(peer.m_rpc, connection));
            if (server == null || !connection.IsConnected()) return;
            long receivedCycle = message.ReadLong(); int receivedPhase = message.ReadInt(), transition = message.ReadInt();
            long started = message.ReadLong(), now = message.ReadLong();
            if (receivedPhase < 0 || receivedPhase > Released || transition < 0 || transition > 2 || receivedCycle < cycle ||
                receivedCycle == cycle && receivedPhase < phase) return;
            stateKnown = true;
            if (receivedCycle != cycle)
            {
                if (Active) { ResumeSpawning(cycle, worldStarted, now); ResumeSimulation(); }
                cycle = receivedCycle; participant = readySent = checkingFrame = false; renderedFrame = -1; transitionCycle = 0;
                phase = transitionPhase = 0;
            }
            bool wasActive = Active;
            phase = receivedPhase; worldStarted = started;
            if (Active && !wasActive) { SuspendSpawning(); SuspendSimulation(); }
            if (!Active)
            {
                ResumeSpawning(cycle, worldStarted, now); ResumeSpawnRoutes(); ResumeSimulation();
                participant = checkingFrame = false;
                return;
            }
            ApplyTransition(transition, server.m_uid);
        }

        private static void ApplyTransition(int transition, long sender)
        {
            if (transition == 0 || !Game.instance) return;
            nativeTransition = true;
            try
            {
                if (transition == 1 && transitionCycle != cycle)
                {
                    transitionCycle = cycle; transitionPhase = 1; participant = Player.m_localPlayer;
                    NativeStart(Game.instance, sender);
                    if (!participant && !ZNet.instance.IsServer()) ZNet.instance.GetServerPeer().m_rpc.Invoke(ReadyMessage, cycle, 1);
                }
                else if (transition == 2 && transitionCycle == cycle && transitionPhase == 1)
                {
                    transitionPhase = 2;
                    NativeStop(Game.instance, sender);
                }
            }
            finally { nativeTransition = false; }
        }

        private static void ReceiveReady(ZRpc connection, long receivedCycle, int kind)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || receivedCycle != cycle || !Active || !Waiting.Contains(connection)) return;
            var peer = ZNet.instance.GetPeers().FirstOrDefault(value => ReferenceEquals(value.m_rpc, connection));
            if (peer == null || !peer.IsReady() || !connection.IsConnected() || kind != 0 && kind != 1 || kind == 0 && phase != Waking) return;
            Waiting.Remove(connection);
            TryRelease();
        }

        private static void Disconnected(ZNetPeer peer)
        {
            Waiting.Remove(peer.m_rpc);
            TryRelease();
        }

        private static void TryRelease()
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || phase != Waking || Waiting.Count != 0 || localWaiting) return;
            ResumeSpawning(cycle, worldStarted, ZNet.instance.GetTime().Ticks); ResumeSimulation();
            phase = Released; participant = false;
            foreach (var peer in ZNet.instance.GetPeers()) if (peer.IsReady()) SendState(peer, 0);
        }

        private static bool ViewClear()
        {
            var player = Player.m_localPlayer; var hud = Hud.instance;
            return player && hud && !player.IsSleeping() && !player.InBed() && !player.IsDead() && !player.IsTeleporting() &&
                !player.InCutscene() && !Game.instance.WaitingForRespawn() && !Game.instance.IsShuttingDown() && !CinematicsManager.IsPlaying() &&
                !hud.m_loadingScreen.gameObject.activeInHierarchy && hud.m_loadingScreen.alpha <= 0f;
        }

        private static void Rendered(Camera camera)
        {
            if (phase == Waking && participant && camera == Utils.GetMainCamera() && ViewClear()) renderedFrame = Time.frameCount;
        }

        private static void AfterFade()
        {
            if (phase != Waking || !participant || readySent || checkingFrame || !ViewClear()) return;
            checkingFrame = true;
            runner.StartCoroutine(ClearFrame(cycle));
        }

        private static IEnumerator ClearFrame(long expectedCycle)
        {
            yield return new WaitForEndOfFrame();
            if (cycle != expectedCycle) yield break;
            checkingFrame = false;
            if (phase != Waking || !participant || readySent || renderedFrame != Time.frameCount || !ViewClear()) yield break;
            readySent = true;
            if (ZNet.instance.IsServer()) { localWaiting = false; TryRelease(); }
            else ZNet.instance.GetServerPeer().m_rpc.Invoke(ReadyMessage, cycle, 0);
        }

        private static void WatchReadiness()
        {
            if (!ZNet.instance.IsServer() || phase != Waking) return;
            if (Time.unscaledTime >= releaseAt)
            {
                log.LogWarning($"Sleep cycle {cycle} stopped waiting for {Waiting.Count + (localWaiting ? 1 : 0)} clear wake-up screens after {ReleaseLimit} seconds.");
                Waiting.Clear(); localWaiting = false;
                TryRelease();
            }
            else if (Time.unscaledTime >= noticeAt)
            {
                noticeAt = Time.unscaledTime + 60f;
                log.LogWarning($"Sleep cycle {cycle} is waiting for {Waiting.Count + (localWaiting ? 1 : 0)} clear wake-up screens.");
            }
        }

        private static void LeaveWorld()
        {
            if (Active) ResumeSpawning(cycle, worldStarted, ZNet.instance.GetTime().Ticks);
            ResumeSimulation(); ClearSpawning(); ClearSpawnRoutes(); ClearModSpawns();
            Waiting.Clear(); cycle = worldStarted = transitionCycle = 0; phase = transitionPhase = 0;
            stateKnown = participant = localWaiting = readySent = checkingFrame = nativeTransition = false;
            renderedFrame = -1;
        }

        private static partial void InitializeSimulation(IntegrationPatches patches);
        private static partial void SuspendSimulation();
        private static partial void ResumeSimulation();
        private static partial void InitializeSpawning(IntegrationPatches patches);
        private static partial void SuspendSpawning();
        private static partial void ResumeSpawning(long sleepCycle, long started, long stopped);
        private static partial void ClearSpawning();
    }
}
