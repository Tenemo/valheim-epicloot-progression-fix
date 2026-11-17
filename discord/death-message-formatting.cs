using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class DeathMessageFormatting
    {
        private const string PlayerColor = "<color=#FF5555>";
        private static PropertyInfo untranslatedNameOf;
        private static ManualLogSource logger;
        private static string quipsDirectory;
        private static readonly System.Random random = new System.Random();

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.DiscordBot, out var plugin)) return;
            logger = log;
            var assembly = plugin.Instance.GetType().Assembly;
            var death = AccessTools.Method(assembly.GetType("DiscordBot.Notices.OnDeath+Player_OnDeath_Patch"), "Prefix");
            var quip = AccessTools.Method(assembly.GetType("DiscordBot.DeathQuips"), "GenerateDeathQuip");
            var environmentalQuip = AccessTools.Method(assembly.GetType("DiscordBot.DeathQuips"), "GenerateEnvironmentalQuip");
            if (death == null || quip == null || environmentalQuip == null)
                throw new InvalidOperationException("DiscordBot death-message API changed.");
            if (StarLevelSystem.Installed)
            {
                untranslatedNameOf = AccessTools.Property(StarLevelSystem.CharacterCacheEntry, "CreatureNameLocalizable");
                if (untranslatedNameOf == null) throw new InvalidOperationException("StarLevelSystem's creature names changed.");
            }
            harmony.Patch(death, transpiler: new HarmonyMethod(typeof(DeathMessageFormatting), nameof(UseEnglishCreatureName)));
            var playerColor = new HarmonyMethod(typeof(DeathMessageFormatting), nameof(ColorPlayerName));
            harmony.Patch(quip, prefix: playerColor,
                transpiler: new HarmonyMethod(typeof(DeathMessageFormatting), nameof(UseCompactStars)));
            var directory = AccessTools.Field(assembly.GetType("DiscordBot.DiscordBotPlugin"), "directory")?.GetValue(null);
            if (!(directory?.GetType().GetField("Path")?.GetValue(directory) is string folder)) throw new InvalidOperationException("DiscordBot's configuration folder changed.");
            quipsDirectory = Path.Combine(folder, "Quips");
            harmony.Patch(environmentalQuip, prefix: playerColor, postfix: new HarmonyMethod(typeof(DeathMessageFormatting), nameof(NameCulprit)));
            log.LogInfo("DiscordBot death reports name the killer in English.");
        }

        // Replace the player placeholder before interpolation, including repeated names and environmental deaths.
        private static void ColorPlayerName(ref string playerName) =>
            playerName = PlayerColor + DiscordSystemMessages.StripTags(playerName) + "</color>";

        internal static string ForDiscord(string content)
        {
            // Plain markers, not ANSI colors: Discord's notifications and mobile app show color codes literally.
            content = Regex.Replace(Localization.instance.Localize(content), PlayerColor + "(.*?)</color>", ">>$1<<", RegexOptions.Singleline);
            // Discord ANSI blocks do not render the game's italic markup.
            return DiscordSystemMessages.Box(content.Replace("<i>", "").Replace("</i>", ""));
        }

        // Runs on the dying player's game, which shows the killer's name in that player's language. Discord gets it in
        // English, from the same untranslated name: StarLevelSystem's, with its affixes, when it has one. A name the game
        // shows in place of that translation, such as a tame's given name or another player's, stays as shown, and so
        // does a name with a word English lacks. Discord cannot render Unity tags.
        private static string CreatureName(Character creature)
        {
            string name = creature.GetHoverName();
            try
            {
                string untranslated = StarLevelSystem.Installed && StarLevelSystem.CacheEntryOf(creature) is object entry &&
                    untranslatedNameOf.GetValue(entry) is string cached ? cached : creature.m_name;
                if (Localization.instance.Localize(untranslated) == name && EnglishText.TryTranslate(untranslated, out string english))
                    name = english;
            }
            catch (Exception error)
            {
                // The report still goes out, with the name as shown.
                logger.LogError("Could not name the killer in English: " + error.GetBaseException().Message);
            }
            return "<i>" + DiscordSystemMessages.StripTags(name).Trim() + "</i>";
        }

        private static string Stars(char symbol, int count) => count > 4 ? count.ToString() + symbol : new string(symbol, count);

        // A fatal burn or poison tick names no attacker, so DiscordBot tells its fire or poison joke. When a creature
        // started that effect, a joke from BurningCreature.txt or PoisonedCreature.txt names it instead; the files are read
        // at the death, so edits apply at once. Without the file, a line or the creature, DiscordBot's own joke stays.
        private static void NameCulprit(string playerName, HitData.HitType hitType, ref string __result)
        {
            if (hitType != HitData.HitType.Burning && hitType != HitData.HitType.Poisoned) return;
            var culprit = DeathAttribution.FatalCulprit(Player.m_localPlayer);
            if (culprit == null) return;
            string file = Path.Combine(quipsDirectory, hitType == HitData.HitType.Burning ? "BurningCreature.txt" : "PoisonedCreature.txt");
            try
            {
                var lines = File.Exists(file) ? File.ReadAllLines(file).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray() : null;
                if (lines == null || lines.Length == 0) { logger.LogWarning("No death jokes naming the creature in " + file + "; DiscordBot's own joke is used."); return; }
                var creature = ZNetScene.instance?.FindInstance(culprit.Id)?.GetComponent<Character>();
                string name = creature != null ? CreatureName(creature) : PrefabName(culprit.Prefab);
                if (name == null) return;
                int level = creature != null ? creature.GetLevel() : culprit.Level;
                __result = lines[random.Next(lines.Length)].Replace("{player}", playerName).Replace("{creature}", name)
                    .Replace("{level}", level > 1 ? " " + Stars('★', level - 1) : "");
            }
            catch (Exception error) { logger.LogError("Could not name the creature behind a fatal burn or poison: " + error.GetBaseException().Message); }
        }

        // A creature no longer loaded is named by its prefab, in English when the game has a translation.
        private static string PrefabName(int prefab)
        {
            var creature = ZNetScene.instance?.GetPrefab(prefab)?.GetComponent<Character>();
            if (creature == null) return null;
            string name = EnglishText.TryTranslate(creature.m_name, out string english) ? english : Localization.instance.Localize(creature.m_name);
            return "<i>" + DiscordSystemMessages.StripTags(name).Trim() + "</i>";
        }

        private static IEnumerable<CodeInstruction> UseEnglishCreatureName(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.LoadsField(AccessTools.Field(typeof(Character), nameof(Character.m_name))),
                AccessTools.Method(typeof(DeathMessageFormatting), nameof(CreatureName)), 1, "DiscordBot killer-name lookup changed.");

        private static IEnumerable<CodeInstruction> UseCompactStars(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions,
                x => x.opcode == OpCodes.Newobj && Equals(x.operand, AccessTools.Constructor(typeof(string), new[] { typeof(char), typeof(int) })),
                AccessTools.Method(typeof(DeathMessageFormatting), nameof(Stars)), 1, "DiscordBot star formatting changed.");
    }
}
