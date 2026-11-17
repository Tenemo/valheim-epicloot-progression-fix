using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The DiscordBot members that several patches use, looked up once, on first use. Without DiscordBot the patches that
    // use them stay off; if it no longer has one of them, the gameplay patches fail at startup.
    internal static class DiscordBot
    {
        internal static Type Discord, Webhook, WebhookRoute, Screenshot, Gateway;
        // Discord.SendMessage(webhook, username, message, hooks, route) posts plain text through a webhook.
        internal static MethodInfo SendMessage;

        internal static void Initialize()
        {
            if (!PluginIds.TryGet(PluginIds.DiscordBot, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var discord = assembly.GetType("DiscordBot.Discord");
            Webhook = assembly.GetType("DiscordBot.Webhook");
            WebhookRoute = assembly.GetType("DiscordBot.WebhookRoute");
            Screenshot = assembly.GetType("DiscordBot.Screenshot");
            Gateway = assembly.GetType("DiscordBot.DiscordGatewayClient");
            SendMessage = Webhook == null || WebhookRoute == null ? null
                : AccessTools.Method(discord, "SendMessage", new[] { Webhook, typeof(string), typeof(string), typeof(List<string>), WebhookRoute });
            if (SendMessage == null || !Webhook.IsEnum || !WebhookRoute.IsEnum)
                throw new InvalidOperationException("DiscordBot's webhook messages changed.");
            if (Screenshot == null) throw new InvalidOperationException("DiscordBot's death screenshots changed.");
            if (Gateway == null) throw new InvalidOperationException("DiscordBot's gateway client changed.");
            Discord = discord;
        }

        internal static bool Installed => Discord != null;
    }
}
