using System;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class DiscordOnlyNotifications
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.DiscordBot, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var discordParameters = new[] { typeof(string), typeof(string), typeof(bool) };
            var aiParameters = new[] { typeof(string), typeof(string) };
            var targets = new[]
            {
                ("DiscordBot.Discord", "Internal_BroadcastMessage", discordParameters),
                ("DiscordBot.Discord", "BroadcastMessage", discordParameters),
                ("DiscordBot.Discord", "DisplayChatMessage", discordParameters),
                ("DiscordBot.ChatAI", "BroadcastMessage", aiParameters),
                ("DiscordBot.ChatAI", "DisplayChatMessage", aiParameters)
            };
            foreach (var target in targets)
            {
                var method = AccessTools.Method(assembly.GetType(target.Item1), target.Item2, target.Item3);
                if (method == null) throw new InvalidOperationException("DiscordBot in-game relay API changed: " + target.Item2);
                string prefix = target.Item1 == "DiscordBot.Discord" ? nameof(OnlyDiscordChat) : nameof(SkipGameRelay);
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(DiscordOnlyNotifications), prefix));
            }
        }

        // DiscordBot sets showDiscord for incoming Discord chat, after rejecting bot authors and other channels.
        // Death and new-day notices pass false. Filter both broadcast and display so their RPCs stay silent too.
        private static bool OnlyDiscordChat(bool showDiscord) => showDiscord;

        // AI replies and quips remain Discord-only. Webhook dispatch and native player chat are separate.
        private static bool SkipGameRelay() => false;
    }
}
