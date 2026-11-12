using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EpicLootProgressionFix
{
    // DiscordBot parses each gateway receive as a whole message, so a message Discord sends in several frames, or one longer
    // than its 16 KiB buffer, is lost. Its listener receives complete text messages instead: frames are joined until the end
    // of the message and decoded only then, so a character split between frames stays whole. Close and other messages pass
    // as they arrive, and a message that is too long or not valid JSON is reported and skipped.
    internal static class DiscordGatewayMessages
    {
        internal const int MaximumLength = 8 << 20;
        // Each listener has its own receive buffer; its complete message waits here until the listener decodes it.
        private static readonly ConditionalWeakTable<byte[], string> Complete = new ConditionalWeakTable<byte[], string>();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static ManualLogSource logger;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!DiscordBot.Installed) return;
            logger = log;
            var listen = AccessTools.Method(DiscordBot.Gateway, "ListenForMessages", new[] { typeof(ClientWebSocket), typeof(CancellationToken) });
            var step = listen == null ? null : AccessTools.EnumeratorMoveNext(listen);
            if (step == null) throw new InvalidOperationException("DiscordBot's gateway listener changed.");
            harmony.Patch(step, transpiler: new HarmonyMethod(typeof(DiscordGatewayMessages), nameof(WholeMessages)));
        }

        private static IEnumerable<CodeInstruction> WholeMessages(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var receive = code.Where(instruction => instruction.operand is System.Reflection.MethodInfo method && method.Name == nameof(WebSocket.ReceiveAsync) &&
                typeof(WebSocket).IsAssignableFrom(method.DeclaringType)).ToList();
            var decode = code.Where(instruction => instruction.operand is System.Reflection.MethodInfo method && method.Name == nameof(Encoding.GetString) &&
                typeof(Encoding).IsAssignableFrom(method.DeclaringType) && method.GetParameters().Length == 3).ToList();
            if (receive.Count != 1 || decode.Count != 1) throw new InvalidOperationException("DiscordBot's gateway receive changed.");
            receive[0].opcode = System.Reflection.Emit.OpCodes.Call;
            receive[0].operand = AccessTools.Method(typeof(DiscordGatewayMessages), nameof(Receive));
            decode[0].opcode = System.Reflection.Emit.OpCodes.Call;
            decode[0].operand = AccessTools.Method(typeof(DiscordGatewayMessages), nameof(Text));
            return code;
        }

        // The next complete message. A text message's frames are joined in this call's own stream, which is dropped with it
        // when the connection closes, is cancelled or is replaced before the message ends.
        internal static async Task<WebSocketReceiveResult> Receive(WebSocket socket, ArraySegment<byte> buffer, CancellationToken token)
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType != WebSocketMessageType.Text) return result;
                var message = new MemoryStream();
                bool oversized = false;
                while (true)
                {
                    oversized |= message.Length + result.Count > MaximumLength;
                    if (!oversized) message.Write(buffer.Array, buffer.Offset, result.Count);
                    if (result.EndOfMessage) break;
                    result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    if (result.MessageType != WebSocketMessageType.Text) return result;
                }
                if (oversized) { logger.LogError($"Skipped a Discord gateway message longer than {MaximumLength} bytes."); continue; }
                string text;
                try
                {
                    text = Utf8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    JToken.Parse(text);
                }
                catch (Exception error) when (error is DecoderFallbackException || error is JsonException)
                {
                    logger.LogError("Skipped a Discord gateway message that is not valid UTF-8 JSON: " + error.Message);
                    continue;
                }
                Complete.Remove(buffer.Array);
                Complete.Add(buffer.Array, text);
                return new WebSocketReceiveResult(Math.Min((int)message.Length, buffer.Count), WebSocketMessageType.Text, true);
            }
        }

        // The listener's decoding of its buffer: the complete message received for it.
        internal static string Text(Encoding encoding, byte[] buffer, int index, int count)
        {
            if (!Complete.TryGetValue(buffer, out string text)) return encoding.GetString(buffer, index, count);
            Complete.Remove(buffer);
            return text;
        }
    }
}
