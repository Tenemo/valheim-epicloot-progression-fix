using System;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // DiscordBot 1.4.9 wraps every player-death report in a Discord embed: a "<player> has died!"
    // title, the killer's icon and an embed timestamp around the joke. The pack wants only the joke text.
    internal static class DeathFeedWithoutEmbed
    {
        private static object deathFeed;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!DiscordBot.Installed) return;
            var embed = AccessTools.Method(DiscordBot.Discord, "SendEmbedMessage",
                new[] { DiscordBot.Webhook, typeof(string), typeof(string), typeof(string), typeof(string), DiscordBot.WebhookRoute });
            if (embed == null || !Enum.IsDefined(DiscordBot.Webhook, "DeathFeed")) throw new InvalidOperationException("DiscordBot webhook API changed.");
            deathFeed = Enum.Parse(DiscordBot.Webhook, "DeathFeed");
            harmony.Patch(embed, prefix: new HarmonyMethod(typeof(DeathFeedWithoutEmbed), nameof(SendEmbedMessage)));
        }

        // Runs on the dying player's client. Mark the player's name and box the joke for Discord.
        // Arguments: webhook, title, content, username, thumbnail, route.
        private static bool SendEmbedMessage(object __instance, object[] __args)
        {
            if (!Equals(__args[0], deathFeed)) return true;
            var content = (string)__args[2];
            if (string.IsNullOrWhiteSpace(content)) return true;
            DiscordBot.SendMessage.Invoke(__instance, new object[] { __args[0], __args[3], DeathMessageFormatting.ForDiscord(content), null, __args[5] });
            return false;
        }
    }
}
