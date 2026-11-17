using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace EpicLootProgressionFix
{
    internal static class DiscordMentions
    {
        private sealed class Display { internal string Text; }
        private static readonly ConditionalWeakTable<object, Display> Messages = new ConditionalWeakTable<object, Display>();
        private static readonly Regex UserMention = new Regex(@"<@!?([0-9]+)>", RegexOptions.CultureInvariant);
        private static Type messageType;
        private static MethodInfo removeMentions;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!DiscordBot.Installed) return;
            var assembly = DiscordBot.Discord.Assembly;
            messageType = assembly.GetType("DiscordBot.Message", true);
            var gateway = assembly.GetType("DiscordBot.DiscordGatewayClient", true);
            removeMentions = AccessTools.Method(gateway, "RemoveMentions", new[] { typeof(string) });
            if (removeMentions == null) throw new MissingMethodException("DiscordBot's mention filtering changed.");
            harmony.Patch(AccessTools.Method(gateway, "HandleMessageCreate", new[] { typeof(JObject) }),
                transpiler: new HarmonyMethod(typeof(DiscordMentions), nameof(KeepDisplayNames)));
            harmony.Patch(AccessTools.Method(gateway, "HandleChatMessage", new[] { messageType }),
                transpiler: new HarmonyMethod(typeof(DiscordMentions), nameof(DisplayChatMentions)));
            harmony.Patch(AccessTools.Method(DiscordBot.Discord, "DisplayChatMessage", new[] { typeof(string), typeof(string), typeof(bool) }),
                transpiler: new HarmonyMethod(typeof(DiscordMentions), nameof(KeepLiteralDollars)));
        }

        // Retain display text with this parsed message, without changing the content used by command routing.
        private static IEnumerable<CodeInstruction> KeepDisplayNames(IEnumerable<CodeInstruction> instructions)
        {
            int found = 0;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (!(instruction.operand is MethodInfo method) || method.DeclaringType?.FullName != "Newtonsoft.Json.JsonConvert" ||
                    method.Name != "DeserializeObject" || method.ReturnType != messageType) continue;
                found++;
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DiscordMentions), nameof(Capture)));
            }
            if (found != 1) throw new InvalidOperationException("DiscordBot's gateway message decoding changed.");
        }

        private static IEnumerable<CodeInstruction> DisplayChatMentions(IEnumerable<CodeInstruction> instructions)
        {
            int found = 0;
            foreach (var instruction in instructions)
            {
                if (!instruction.Calls(removeMentions)) { yield return instruction; continue; }
                found++;
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DiscordMentions), nameof(ForChat)));
            }
            if (found != 1) throw new InvalidOperationException("DiscordBot's chat mention forwarding changed.");
        }

        private static void Capture(object message, JObject data)
        {
            string content = Text(data?["content"]);
            if (message != null && content != null && UserMention.IsMatch(content))
                Messages.Add(message, new Display { Text = Resolve(content, data["mentions"] as JArray).Trim() });
        }

        private static string ForChat(string content, object message) =>
            message != null && Messages.TryGetValue(message, out var display) ? display.Text : Resolve(content, null)?.Trim();

        private static string Text(JToken value) => value?.Type == JTokenType.String ? (string)value : null;

        private static IEnumerable<CodeInstruction> KeepLiteralDollars(IEnumerable<CodeInstruction> instructions)
        {
            var localize = AccessTools.Method(typeof(Localization), "Localize", new[] { typeof(string) });
            int found = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(localize))
                {
                    found++;
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(DiscordMentions), nameof(LocalizeChat));
                }
                yield return instruction;
            }
            if (found != 1) throw new InvalidOperationException("DiscordBot's chat localization changed.");
        }

        private static string LocalizeChat(Localization localization, string text) =>
            string.Join("$", text.Split(new[] { "$\u200b" }, StringSplitOptions.None).Select(localization.Localize));

        internal static string Resolve(string content, JArray mentions)
        {
            if (string.IsNullOrEmpty(content)) return content;
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (mentions != null)
                foreach (var value in mentions)
                {
                    if (!(value is JObject user)) continue;
                    string id = Text(user["id"]);
                    if (string.IsNullOrEmpty(id)) continue;
                    string name = Text((user["member"] as JObject)?["nick"]);
                    if (string.IsNullOrWhiteSpace(name)) name = Text(user["global_name"]);
                    if (string.IsNullOrWhiteSpace(name)) name = Text(user["username"]);
                    if (!string.IsNullOrWhiteSpace(name)) names[id] = name;
                }
            return UserMention.Replace(content, match => "@" + (names.TryGetValue(match.Groups[1].Value, out var name)
                // Angle brackets cannot start TMP tags. Mark literal dollars for the receiving chat's localization call.
                ? name.Replace("<", "<\u200b").Replace("$", "$\u200b") : "user-" + match.Groups[1].Value));
        }
    }
}
