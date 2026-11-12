using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;

namespace EpicLootProgressionFix
{
    internal static class IntegrationCompatibility
    {
        private const string CapabilitiesRpc = Plugin.Id + ".integrations.1", RefusalRpc = Plugin.Id + ".integration-refusal.1", PluginsRpc = Plugin.Id + ".client-plugins.1";
        private static readonly HashSet<ZRpc> matched = new HashSet<ZRpc>(), samePlugins = new HashSet<ZRpc>();
        private static ManualLogSource logger;
        private static string reason;
        private static readonly Action<ZNet, ZRpc, int> SetError = AccessTools.MethodDelegate<Action<ZNet, ZRpc, int>>(
            AccessTools.Method(typeof(ZNet), "RPC_Error"));

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            logger = log;
            harmony.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"), postfix: new HarmonyMethod(typeof(IntegrationCompatibility), nameof(Register)));
            foreach (string method in new[] { "RPC_ServerHandshake", "RPC_ClientHandshake" })
                harmony.Patch(AccessTools.Method(typeof(ZNet), method), prefix: new HarmonyMethod(typeof(IntegrationCompatibility), nameof(Exchange)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(ZNet), "RPC_PeerInfo"), prefix: new HarmonyMethod(typeof(IntegrationCompatibility), nameof(AllowPeer)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.Disconnect)), postfix: new HarmonyMethod(typeof(IntegrationCompatibility), nameof(Disconnected)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "Shutdown"), prefix: new HarmonyMethod(typeof(IntegrationCompatibility), nameof(Clear)));
            harmony.Patch(AccessTools.Method(typeof(FejdStartup), "ShowConnectError"), postfix: new HarmonyMethod(typeof(IntegrationCompatibility), nameof(Show)) { priority = Priority.Last });
        }

        private static void Register(ZNet __instance, ZNetPeer peer)
        {
            matched.Remove(peer.m_rpc);
            if (!__instance.IsServer()) reason = null;
            peer.m_rpc.Register<string>(CapabilitiesRpc, (rpc, payload) => Receive(__instance, rpc, payload));
            if (__instance.IsServer()) peer.m_rpc.Register<string>(PluginsRpc, (rpc, report) => ReceivePlugins(__instance, rpc, report));
            peer.m_rpc.Register<string>(RefusalRpc, (rpc, explanation) =>
            {
                if (__instance.IsServer() || !peer.m_server || peer.m_rpc != rpc || explanation == null || explanation.Length > 1024) return;
                reason = explanation;
                SetError(__instance, rpc, (int)ZNet.ConnectionStatus.ErrorConnectFailed);
                __instance.Disconnect(peer);
            });
        }
        private static ZNetPeer Peer(ZNet network, ZRpc rpc) => network.GetPeers().FirstOrDefault(peer => peer.m_rpc == rpc);
        private static bool Exchange(ZNet __instance, ZRpc rpc)
        {
            if (Peer(__instance, rpc) == null) return false;
            if (!Plugin.Ready) { Reject(__instance, rpc, "Progression Fix could not start. Check the game log before joining."); return false; }
            if (__instance.IsServer()) matched.Remove(rpc);
            // Reliable RPC order sends these before the native handshake response / PeerInfo. The admission gate also
            // rejects a missing exchange rather than assuming that a matching mod version implies matching features.
            rpc.Invoke(CapabilitiesRpc, OptionalIntegrations.Capabilities);
            if (!__instance.IsServer()) rpc.Invoke(PluginsRpc, ClientPlugins.Report);
            return true;
        }
        // The expected payload is this format's own, so equality alone also refuses other formats and oversized payloads.
        internal static bool Matches(string expected, string received) => string.Equals(expected, received, StringComparison.Ordinal);
        private static void Receive(ZNet network, ZRpc rpc, string payload)
        {
            if (!network || Peer(network, rpc) == null) return;
            if (!Plugin.Ready) { Reject(network, rpc, "Progression Fix could not start. Check the game log before joining."); return; }
            if (!Matches(OptionalIntegrations.Capabilities, payload))
            {
                string difference = payload != null && payload.Length <= 16384 && payload.StartsWith("1\n", StringComparison.Ordinal) ? OptionalIntegrations.DescribeMismatch(payload) : "";
                Reject(network, rpc, "Install matching supported integrations on the server and client." +
                    (difference.Length == 0 ? "" : "\n" + difference));
                return;
            }
            matched.Add(rpc);
        }
        private static void ReceivePlugins(ZNet network, ZRpc rpc, string report)
        {
            if (!network || Peer(network, rpc) == null) return;
            string difference = ClientPlugins.Difference(report, logger);
            if (difference == null) samePlugins.Add(rpc);
            else Reject(network, rpc, difference);
        }
        private static bool AllowPeer(ZNet __instance, ZRpc rpc)
        {
            // A client from before this check never reports its plugins.
            if (__instance.IsServer() && !samePlugins.Contains(rpc)) { Reject(__instance, rpc, ClientPlugins.Refusal); return false; }
            if (Plugin.Ready && matched.Contains(rpc)) return true;
            Reject(__instance, rpc, "Install matching server and client integrations before joining.");
            return false;
        }
        private static void Reject(ZNet network, ZRpc rpc, string explanation)
        {
            var peer = Peer(network, rpc);
            if (peer == null) return;
            matched.Remove(rpc);
            logger.LogWarning(explanation);
            string displayed = explanation.Length <= 800 ? explanation : explanation.Substring(0, 800) + "… See the log for full details.";
            if (network.IsServer())
            {
                rpc.Invoke(RefusalRpc, displayed);
                rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorConnectFailed);
            }
            else { reason = displayed; SetError(network, rpc, (int)ZNet.ConnectionStatus.ErrorConnectFailed); }
            network.Disconnect(peer);
        }
        private static void Disconnected(ZNetPeer peer) { matched.Remove(peer.m_rpc); samePlugins.Remove(peer.m_rpc); }
        private static void Clear() { matched.Clear(); samePlugins.Clear(); }
        private static void Show(ZNet.ConnectionStatus statusOverride, TMP_Text ___m_connectionFailedError)
        {
            var status = statusOverride == ZNet.ConnectionStatus.None ? ZNet.GetConnectionStatus() : statusOverride;
            if (string.IsNullOrEmpty(reason) || status == ZNet.ConnectionStatus.None || status == ZNet.ConnectionStatus.Connecting || status == ZNet.ConnectionStatus.Connected) return;
            ConnectionFailureDetails.Append(___m_connectionFailedError, reason, plainText: true);
            reason = null;
        }
    }
}
