using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;

namespace EpicLootProgressionFix
{
    // The game's English text whatever language the player chose: its own English words, then the English words mods add
    // through Jotunn, such as StarLevelSystem's modifier names, which replace the game's as they do for English players.
    internal static class EnglishText
    {
        // A word runs from $ to the first of the game's word-ending characters, as in Localization.FindNextWord.
        private static readonly Regex Word = new Regex(@"\$([^ (){}\[\]+\-!?/\\&%,.:=<>\n]+)");
        // Resolved with the class, which the plugin does at startup for every class, so a changed API stops the add-on there.
        private static readonly FieldInfo SettingsField = AccessTools.Field(typeof(Localization), "m_localizationSettings") ??
            throw new InvalidOperationException("Game localization settings changed.");
        private static readonly Func<Localization, StringReader, List<List<string>>> ReadCsv =
            AccessTools.MethodDelegate<Func<Localization, StringReader, List<List<string>>>>(
                AccessTools.Method(typeof(Localization), "DoQuoteLineSplit", new[] { typeof(StringReader) }) ??
                throw new InvalidOperationException("Game localization parser changed."));
        private static readonly FieldInfo ModLocalizations = AccessTools.Field(typeof(LocalizationManager), "Localizations") ??
            throw new InvalidOperationException("Jotunn localization API changed.");
        private static Dictionary<string, string> words;
        private static readonly Dictionary<string, Dictionary<string, string>> translations = new Dictionary<string, Dictionary<string, string>>();

        // False when a word has no English text; those words stay as they were.
        internal static bool TryTranslate(string text, out string english)
        {
            var known = Words();
            bool complete = true;
            english = Word.Replace(text ?? "", match =>
            {
                if (known.TryGetValue(match.Groups[1].Value, out string word)) return word;
                complete = false;
                return match.Value;
            });
            return complete;
        }

        // Legacy item names contain already translated words. Read only the requested word, and cache its languages.
        internal static Dictionary<string, string> Translations(string token)
        {
            if (translations.TryGetValue(token, out var saved)) return saved;
            TryTranslate(token, out string english);
            var result = new Dictionary<string, string> { ["English"] = english };
            string key = token.TrimStart('$');
            var languages = new HashSet<string> { "English" };
            var settings = (LocalizationSettings)SettingsField.GetValue(null);
            foreach (var file in settings.Localizations)
            {
                if (file == null) continue;
                using (var reader = new StringReader(file.text))
                {
                    string[] columns = reader.ReadLine().Split(',');
                    for (int column = 1; column < columns.Length; column++)
                        languages.Add(columns[column].Trim().Trim('"'));
                    foreach (var row in ReadCsv(Localization.instance, reader))
                    {
                        if (!string.Equals(row[0], key, StringComparison.OrdinalIgnoreCase)) continue;
                        for (int column = 1; column < columns.Length && column < row.Count; column++)
                            if (!string.IsNullOrWhiteSpace(row[column]))
                                result[columns[column].Trim().Trim('"')] = row[column].Trim();
                        break;
                    }
                }
            }
            foreach (var mod in ((Dictionary<string, CustomLocalization>)ModLocalizations.GetValue(LocalizationManager.Instance)).Values)
                foreach (string language in languages)
                    foreach (var word in mod.GetTranslations(language))
                        if (string.Equals(word.Key, key, StringComparison.OrdinalIgnoreCase)) result[language] = word.Value;
            translations[token] = result;
            return result;
        }

        // Built on first use, in play, after every mod has added its words.
        private static Dictionary<string, string> Words()
        {
            if (words != null) return words;
            var settings = (LocalizationSettings)SettingsField.GetValue(null);
            if (settings == null) throw new InvalidOperationException("Game localization is not ready.");
            var english = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in settings.Localizations)
            {
                if (file == null) continue;
                using (var reader = new StringReader(file.text))
                {
                    string[] columns = reader.ReadLine().Split(',');
                    int column = Array.FindIndex(columns, name => name.Trim().Trim('"') == "English");
                    if (column < 0) continue;
                    foreach (var row in ReadCsv(Localization.instance, reader))
                        if (row.Count > column && row[0].Length != 0 && !row[0].StartsWith("//"))
                            english[row[0]] = row[column].Trim();
                }
            }
            // In Jotunn's order, so a later mod's word replaces an earlier one's, as in the game.
            foreach (var mod in ((Dictionary<string, CustomLocalization>)ModLocalizations.GetValue(LocalizationManager.Instance)).Values)
                foreach (var word in mod.GetTranslations("English"))
                    english[word.Key] = word.Value;
            return words = english;
        }
    }
}
