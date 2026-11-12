using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class AchievementAnnouncements
    {
        private static readonly HashSet<string> ObservedUnlocks = new HashSet<string>();
        private static FieldInfo discordInstance;
        private static object chat;
        private static object defaultRoute;
        private static ManualLogSource logger;
        [ThreadStatic] private static int suppressedUnlocks;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!DiscordBot.Installed) return;
            logger = log;
            discordInstance = AccessTools.Field(DiscordBot.Discord, "instance");
            if (discordInstance == null) throw new InvalidOperationException("DiscordBot achievement relay API changed.");
            chat = Enum.Parse(DiscordBot.Webhook, "Chat");
            defaultRoute = Enum.Parse(DiscordBot.WebhookRoute, "Default");
            harmony.Patch(AccessTools.Method(typeof(Achievements), nameof(Achievements.AchievementEvent)),
                prefix: new HarmonyMethod(typeof(AchievementAnnouncements), nameof(BeforeUnlock)),
                postfix: new HarmonyMethod(typeof(AchievementAnnouncements), nameof(AfterUnlock)),
                finalizer: new HarmonyMethod(typeof(AchievementAnnouncements), nameof(AfterUnlockTree)));
        }

        private static void BeforeUnlock(bool synchronize, out int __state)
        {
            __state = suppressedUnlocks;
            // Vanilla startup and Unshamed's retroactive sweep pass synchronize=false.
            // Keep their nested meta-achievements quiet too, even when those pass true.
            if (!synchronize) suppressedUnlocks++;
        }

        private static void AfterUnlockTree(int __state) => suppressedUnlocks = __state;

        private static void AfterUnlock(Achievement ach)
        {
            if (ach == null || !ObservedUnlocks.Add(ach.m_id)) return;
            if (suppressedUnlocks != 0 || !Plugin.DiscordAchievements.Value || !Plugin.Ready) return;
            var player = Player.m_localPlayer;
            var server = ZNet.instance != null ? ZNet.instance.GetServerRPC() : null;
            if (player == null || server == null || !server.IsConnected()) return;

            try
            {
                var instance = discordInstance.GetValue(null);
                if (instance == null) throw new InvalidOperationException("DiscordBot is not ready to relay the achievement.");
                string title = English(ach.m_name);
                string requirement = English(ach.m_description);
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(requirement))
                    throw new InvalidOperationException($"Achievement {ach.m_id} has no English name or requirement description.");
                string playerName = PlainText(player.GetPlayerName());
                string message = $"{playerName} earned the achievement \"{title}\" [{requirement}]";
                DiscordBot.SendMessage.Invoke(instance, new object[] { chat, playerName, DiscordSystemMessages.Box(message), null, defaultRoute });
            }
            catch (Exception error)
            {
                // A failed announcement must not interrupt the game's achievement award.
                logger.LogError($"Could not announce achievement {ach.m_id}: {error.GetBaseException().Message}");
            }
        }

        private static string English(string text)
        {
            if (!EnglishText.TryTranslate(text, out string translated))
                throw new InvalidOperationException($"Missing English achievement text: {translated}");
            return PlainText(translated);
        }

        // Discord shows code-block text literally, so no markdown escaping; Box() breaks up backtick runs.
        private static string PlainText(string text)
        {
            text = DiscordSystemMessages.StripTags(text).Replace("\\n", " ");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }
    }
}
