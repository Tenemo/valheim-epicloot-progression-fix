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
            // With DiscordBot's death screenshot or GIF on, the picture needs the embed, so only its text is converted.
            foreach (string sender in new[] { "SendImageMessage", "SendGifMessage" })
                harmony.Patch(AccessTools.Method(DiscordBot.Discord, sender) ?? throw new InvalidOperationException("DiscordBot webhook API changed."),
                    prefix: new HarmonyMethod(typeof(DeathFeedWithoutEmbed), nameof(CaptionPicture)));
        }

        // The first argument is the webhook.
        private static void CaptionPicture(object __0, ref string content)
        {
            if (Equals(__0, deathFeed) && !string.IsNullOrWhiteSpace(content)) content = DeathMessageFormatting.ForDiscord(content);
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
