using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Shows the online count and character names in DiscordBot's custom status.
    // The native queue and listener run as Unity coroutines. A queued intent reads the roster when its send starts.
    internal static class DiscordPlayerStatus
    {
        private const float MinimumInterval = 15f;
        private const float CloseAssociationWindow = 5f;
        private const float MaximumRetryDelay = 300f;
        private static FieldInfo gatewayInstance, gatewaySocket, gatewayConnected, gatewayQueue, gatewayReconnect;
        private static MethodInfo enqueue, botToken;
        private static ManualLogSource log;
        private static ClientWebSocket sessionSocket;
        private static string sentStatus;
        private static float sentAt = float.NegativeInfinity;
        private static float retryAt = float.NegativeInfinity;
        private static int failures;
        private static PresenceRequest pending, lastAttempt;
        [ThreadStatic] private static PresenceRequest activeSend;

        [JsonObject(MemberSerialization.OptIn)]
        private sealed class PresenceRequest
        {
            [JsonProperty("op")] public int Opcode => 3;
            [JsonProperty("d")] public object Data;
            internal readonly ClientWebSocket Socket;
            internal string Text;
            internal Task SendTask;
            internal float StartedAt;
            internal bool FailureRecorded;
            internal PresenceRequest(ClientWebSocket socket) => Socket = socket;
        }

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource logger)
        {
            if (!PluginIds.TryGet(PluginIds.DiscordBot, out var plugin)) return;
            var gateway = plugin.Instance.GetType().Assembly.GetType("DiscordBot.DiscordGatewayClient");
            gatewayInstance = AccessTools.Field(gateway, "instance");
            gatewaySocket = AccessTools.Field(gateway, "websocket");
            gatewayConnected = AccessTools.Field(gateway, "isConnected");
            gatewayReconnect = AccessTools.Field(gateway, "shouldReconnect");
            gatewayQueue = AccessTools.Field(gateway, "messageQueue");
            enqueue = gatewayQueue == null ? null : AccessTools.Method(gatewayQueue.FieldType, "Enqueue");
            botToken = AccessTools.PropertyGetter(plugin.Instance.GetType(), "BOT_TOKEN");
            var dispatch = AccessTools.Method(gateway, "HandleDispatchEvent");
            var closed = AccessTools.Method(gateway, "DisconnectWebSocket");
            var sending = AccessTools.Method(gateway, "SendGatewayMessage", new[] { typeof(object) });
            var sendStep = sending == null ? null : AccessTools.EnumeratorMoveNext(sending);
            if (gatewayInstance == null || gatewaySocket == null || gatewayConnected?.FieldType != typeof(bool) || gatewayReconnect?.FieldType != typeof(bool) ||
                enqueue == null || dispatch == null || closed == null || sendStep == null || botToken?.ReturnType != typeof(string))
                throw new InvalidOperationException("DiscordBot gateway API changed.");
            log = logger;
            harmony.Patch(dispatch, postfix: new HarmonyMethod(typeof(DiscordPlayerStatus), nameof(SessionStarted)));
            harmony.Patch(closed, prefix: new HarmonyMethod(typeof(DiscordPlayerStatus), nameof(ConnectionClosed)));
            harmony.Patch(sending, postfix: new HarmonyMethod(typeof(DiscordPlayerStatus), nameof(PrepareSend)));
            harmony.Patch(sendStep, transpiler: new HarmonyMethod(typeof(DiscordPlayerStatus), nameof(ObserveSend)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "SendPlayerList"),
                postfix: new HarmonyMethod(typeof(DiscordPlayerStatus), nameof(SendStatus)));
            logger.LogInfo("DiscordBot's Discord status shows the online count and character names.");
        }

        private static void SessionStarted(object __instance, string __0, JObject __1)
        {
            if ((__0 != "READY" && __0 != "RESUMED") || __1 == null ||
                !ReferenceEquals(gatewayInstance.GetValue(null), __instance)) return;
            sessionSocket = (ClientWebSocket)gatewaySocket.GetValue(__instance);
            sentStatus = null;
            pending = null;
            if (failures > 0) log.LogInfo("Discord player status is authenticated again; the current roster will refresh after its retry cooldown.");
        }

        private static string SafeReason(string reason)
        {
            string token = (string)botToken.Invoke(null, null);
            string text = reason ?? "no close reason supplied";
            if (!string.IsNullOrEmpty(token)) text = text.Replace(token, "[redacted]");
            return new string(text.Select(character => char.IsControl(character) ? ' ' : character).Take(200).ToArray());
        }

        private static void ConnectionClosed(object __instance)
        {
            if (!ReferenceEquals(gatewayInstance.GetValue(null), __instance)) return;
            var socket = (ClientWebSocket)gatewaySocket.GetValue(__instance);
            if (socket == null) return;
            string reason = "close code " + (socket.CloseStatus.HasValue ? ((int)socket.CloseStatus.Value).ToString() : "not supplied") +
                ": " + SafeReason(socket.CloseStatusDescription);
            bool reconnect = (bool)gatewayReconnect.GetValue(__instance);
            if (reconnect && lastAttempt != null && ReferenceEquals(lastAttempt.Socket, socket) &&
                Time.realtimeSinceStartup - lastAttempt.StartedAt <= CloseAssociationWindow)
                Retry(lastAttempt, "connection interrupted near a status update; " + reason);
            else log.LogInfo((reconnect ? "Discord player status awaits READY or RESUMED; " :
                "Discord player status is disconnected and DiscordBot has paused reconnection; ") + reason + ".");
            sessionSocket = null;
            sentStatus = null;
            pending = null;
        }

        private static void Retry(PresenceRequest request, string reason)
        {
            if (request != null && request.FailureRecorded) return;
            if (request != null) request.FailureRecorded = true;
            failures = Math.Min(failures + 1, 5);
            float delay = Mathf.Min(MaximumRetryDelay, MinimumInterval * (1 << failures));
            retryAt = Time.realtimeSinceStartup + delay;
            sentStatus = null;
            log.LogWarning($"Discord player status retry in {delay:0} seconds: {SafeReason(reason)}.");
        }

        private static ClientWebSocket AuthenticatedSocket(object gateway)
        {
            if (gateway == null || !ReferenceEquals(gatewayInstance.GetValue(null), gateway) ||
                !(bool)gatewayConnected.GetValue(gateway)) return null;
            var socket = (ClientWebSocket)gatewaySocket.GetValue(gateway);
            return socket != null && ReferenceEquals(socket, sessionSocket) && socket.State == WebSocketState.Open ? socket : null;
        }

        private static void PrepareSend(object __instance, object __0, ref IEnumerator __result)
        {
            if (__0 is PresenceRequest request) __result = SendPresence(__instance, request, __result);
        }

        private static IEnumerable<CodeInstruction> ObserveSend(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var sends = code.Where(instruction => instruction.operand is MethodInfo method && method.Name == nameof(WebSocket.SendAsync) &&
                typeof(WebSocket).IsAssignableFrom(method.DeclaringType) && method.ReturnType == typeof(Task) &&
                method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] {
                    typeof(ArraySegment<byte>), typeof(WebSocketMessageType), typeof(bool), typeof(CancellationToken) })).ToArray();
            if (sends.Length != 1) throw new InvalidOperationException("DiscordBot's gateway send operation changed.");
            sends[0].opcode = OpCodes.Call;
            sends[0].operand = AccessTools.Method(typeof(DiscordPlayerStatus), nameof(SendObserved));
            return code;
        }

        private static Task SendObserved(WebSocket socket, ArraySegment<byte> buffer, WebSocketMessageType kind, bool final, CancellationToken cancellation)
        {
            var request = activeSend;
            if (request != null)
            {
                lastAttempt = request;
                request.StartedAt = sentAt = Time.realtimeSinceStartup;
            }
            var task = socket.SendAsync(buffer, kind, final, cancellation);
            if (request != null) request.SendTask = task;
            return task;
        }

        private static IEnumerator SendPresence(object gateway, PresenceRequest request, IEnumerator native)
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (!ReferenceEquals(pending, request) || !ReferenceEquals(AuthenticatedSocket(gateway), request.Socket) ||
                    now < retryAt || now - sentAt < MinimumInterval || !ZNet.instance) yield break;
                request.Text = StatusText(ZNet.instance.GetPlayerList());
                if (request.Text == sentStatus) yield break;
                request.Data = new
                {
                    since = (long?)null,
                    activities = new[] { new { name = "Custom Status", type = 4, state = request.Text } },
                    status = "online",
                    afk = false
                };
                while (true)
                {
                    bool more;
                    var previous = activeSend;
                    try { activeSend = request; more = native.MoveNext(); }
                    catch (Exception error) { Retry(request, error.GetBaseException().Message); yield break; }
                    finally { activeSend = previous; }
                    if (!more) break;
                    yield return native.Current;
                }
                if (!ReferenceEquals(AuthenticatedSocket(gateway), request.Socket)) yield break;
                if (request.SendTask?.Status == TaskStatus.RanToCompletion)
                    sentStatus = request.Text;
                else
                {
                    string reason = request.SendTask?.Exception?.GetBaseException().Message ??
                        (request.SendTask?.IsCanceled == true ? "send cancelled" : "send did not complete");
                    Retry(request, reason);
                }
            }
            finally
            {
                (native as IDisposable)?.Dispose();
                if (ReferenceEquals(pending, request)) pending = null;
            }
        }

        private static string StatusText(List<ZNet.PlayerInfo> players)
        {
            if (players.Count == 0) return "Nobody online";
            var names = players.Select(player =>
            {
                var name = new string((player.m_name ?? "").Select(character =>
                    char.IsControl(character) || char.IsWhiteSpace(character) ? ' ' : character).ToArray()).Trim();
                return name.Length == 0 ? "Unnamed" : name;
            }).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ThenBy(name => name, StringComparer.Ordinal).ToArray();
            string prefix = players.Count + " online: ";
            for (int shown = names.Length; shown > 0; shown--)
            {
                int omitted = names.Length - shown;
                string text = prefix + string.Join(", ", names.Take(shown)) + (omitted == 0 ? "" : " +" + omitted + " more");
                if (text.Length <= 128) return text;
            }
            return prefix + "+" + names.Length + " more";
        }

        private static void SendStatus(ZNet __instance)
        {
            PresenceRequest request = null;
            try
            {
                var gateway = gatewayInstance.GetValue(null);
                var socket = AuthenticatedSocket(gateway);
                if (socket == null) return;
                float now = Time.realtimeSinceStartup;
                if (sentStatus != null && now - sentAt > CloseAssociationWindow) failures = 0;
                if (pending != null || now < retryAt || now - sentAt < MinimumInterval) return;
                string text = StatusText(__instance.GetPlayerList());
                if (text == sentStatus) return;
                request = pending = new PresenceRequest(socket);
                enqueue.Invoke(gatewayQueue.GetValue(gateway), new object[] { request, false });
            }
            catch (Exception error)
            {
                if (ReferenceEquals(pending, request)) pending = null;
                Retry(request, error.GetBaseException().Message);
            }
        }
    }
}
