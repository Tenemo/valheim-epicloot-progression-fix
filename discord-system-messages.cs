using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class DiscordSystemMessages
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (PluginIds.TryGet(PluginIds.DiscordConnector, out var connector))
            {
                var payload = connector.Instance.GetType().Assembly.GetType("DiscordConnector.DiscordExecuteWebhook");
                var send = AccessTools.Method(payload, "SendFor");
                if (send == null) throw new InvalidOperationException("DiscordConnector message API changed.");
                harmony.Patch(send, prefix: new HarmonyMethod(typeof(DiscordSystemMessages), nameof(ConnectorMessage)));
            }
            if (DiscordBot.Installed)
            {
                var send = AccessTools.Method(DiscordBot.Discord, "DispatchWebhook");
                if (send == null) throw new InvalidOperationException("DiscordBot message API changed.");
                harmony.Patch(send, prefix: new HarmonyMethod(typeof(DiscordSystemMessages), nameof(BotMessage)));
            }
        }

        internal static string Box(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            // Already-boxed messages, such as death reports, are not wrapped twice.
            if (text.StartsWith("```ansi\n", StringComparison.Ordinal) && text.EndsWith("\n```", StringComparison.Ordinal))
                return text;
            return "```ansi\n" + BreakCodeFences(text.Replace("\u001b", "")) + "\n```";
        }

        // A zero-width space between adjacent backticks keeps any run of them from closing the code block.
        internal static string BreakCodeFences(string text) => Regex.Replace(text, "`(?=`)", "`\u200b");

        // Discord cannot render the game's rich-text tags, such as color and italics.
        internal static string StripTags(string text) => Regex.Replace(text ?? "", "<[^>]*>", "");

        private static void ConnectorMessage(object __instance, object __0)
        {
            string kind = __0.ToString();
            if (kind == "PlayerShout" || kind == "PlayerFirstShout") return;
            BoxPayload(__instance);
        }

        private static void BotMessage(object[] __args)
        {
            // Chat includes player speech. Our achievement announcements are boxed at their source.
            if (__args[0].ToString() == "Chat") return;
            BoxPayload(__args[2]);
        }

        private static void BoxPayload(object payload)
        {
            BoxMember(payload, "content");
            if (!(Read(payload, "embeds") is IEnumerable embeds)) return;
            foreach (var embed in embeds)
            {
                BoxMember(embed, "description");
                if (!(Read(embed, "fields") is IEnumerable fields)) continue;
                foreach (var field in fields) BoxMember(field, "value");
            }
        }

        // DiscordBot uses fields; DiscordConnector uses properties. Keep routing and embed metadata intact.
        // Plain reflection: AccessTools logs a warning for every member it does not find.
        private static object Read(object instance, string name)
        {
            if (instance == null) return null;
            var field = instance.GetType().GetField(name, Members);
            if (field != null) return field.GetValue(instance);
            var property = instance.GetType().GetProperty(name, Members);
            if (property == null) throw new InvalidOperationException($"Discord message member {name} changed.");
            return property.GetValue(instance);
        }

        private static void BoxMember(object instance, string name)
        {
            if (!(Read(instance, name) is string text) || string.IsNullOrWhiteSpace(text)) return;
            var field = instance.GetType().GetField(name, Members);
            if (field != null) field.SetValue(instance, Box(text));
            else instance.GetType().GetProperty(name, Members).SetValue(instance, Box(text));
        }
    }
}
