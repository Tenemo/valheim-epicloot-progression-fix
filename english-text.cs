using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        // Jotunn keeps each mod's words of a language in a plain dictionary, whose version changes with every word set or removed.
        private static readonly FieldInfo DictionaryVersion = AccessTools.Field(typeof(Dictionary<string, string>), "_version") ??
            throw new InvalidOperationException("Dictionary version field changed.");
        private static Dictionary<string, string> words;
        private static readonly Dictionary<string, Dictionary<string, string>> translations = new Dictionary<string, Dictionary<string, string>>();

        // The game's text files hold every language's words, 5.7 million characters, and reading them makes about 30 MB of
        // garbage in 50-90 ms. They are read once, under the join's loading screen, for their English words, their languages
        // and every language's words for the names of items that can be enchanted, which legacy item names are made of.
        private sealed class GameFiles
        {
            internal readonly Dictionary<string, string> English = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Languages = new HashSet<string> { "English" };
            internal readonly HashSet<string> Kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Only an item that holds one per slot can be enchanted; EpicLoot's name formats complete its legacy names.
            internal bool Keeps(string key) => Kept.Contains(key) || key.StartsWith("mod_epicloot_", StringComparison.OrdinalIgnoreCase);
            // Each kept word's non-empty cells, as language and text, from the first row naming it in each file, in file order;
            // a kept word no file names has none.
            internal readonly Dictionary<string, List<KeyValuePair<string, string>>> Rows =
                new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
        }
        private static GameFiles gameFiles;

        // One mod's words of one language for the kept words, each with the last text the dictionary's order gives it, so a
        // name needs no pass over every mod's words in 35 languages; rebuilt when that dictionary's version moves.
        private sealed class ModWords
        {
            internal int Version;
            internal readonly Dictionary<string, string> Last = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        private static readonly ConditionalWeakTable<Dictionary<string, string>, ModWords> modWords = new ConditionalWeakTable<Dictionary<string, string>, ModWords>();

        internal static void Initialize(IntegrationPatches harmony) =>
            harmony.Patch(AccessTools.Method(typeof(Game), "SpawnPlayer"), postfix: new HarmonyMethod(typeof(EnglishText), nameof(ReadUnderLoadingScreen)));

        private static void ReadUnderLoadingScreen()
        {
            var files = Read();
            foreach (var mod in ((Dictionary<string, CustomLocalization>)ModLocalizations.GetValue(LocalizationManager.Instance)).Values)
                foreach (string language in files.Languages)
                    if (mod.GetTranslations(language) is Dictionary<string, string> map && map.Count != 0) KeptWords(files, map);
        }

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
            var files = Read();
            HashSet<string> languages;
            if (files.Keeps(key))
            {
                if (files.Rows.TryGetValue(key, out var cells))
                    foreach (var cell in cells) result[cell.Key] = cell.Value;
                languages = files.Languages;
            }
            else languages = ReadRows(key, result);
            foreach (var mod in ((Dictionary<string, CustomLocalization>)ModLocalizations.GetValue(LocalizationManager.Instance)).Values)
                foreach (string language in languages)
                {
                    var map = mod.GetTranslations(language);
                    // The last matching word wins, as a pass over every word would leave it.
                    if (files.Keeps(key) && map is Dictionary<string, string> plain)
                    {
                        if (plain.Count != 0 && KeptWords(files, plain).Last.TryGetValue(key, out string text)) result[language] = text;
                    }
                    else
                        foreach (var word in map)
                            if (string.Equals(word.Key, key, StringComparison.OrdinalIgnoreCase)) result[language] = word.Value;
                }
            translations[token] = result;
            return result;
        }

        private static ModWords KeptWords(GameFiles files, Dictionary<string, string> map)
        {
            int version = (int)DictionaryVersion.GetValue(map);
            if (modWords.TryGetValue(map, out var kept) && kept.Version == version) return kept;
            kept = new ModWords { Version = version };
            foreach (var word in map)
                if (files.Keeps(word.Key)) kept.Last[word.Key] = word.Value;
            modWords.Remove(map);
            modWords.Add(map, kept);
            return kept;
        }

        // A word the single read did not keep, such as the name of an item a mod made stackable, reads every file again.
        private static HashSet<string> ReadRows(string key, Dictionary<string, string> result)
        {
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
            return languages;
        }

        private static GameFiles Read()
        {
            if (gameFiles != null) return gameFiles;
            var settings = (LocalizationSettings)SettingsField.GetValue(null);
            if (settings == null) throw new InvalidOperationException("Game localization is not ready.");
            var files = new GameFiles();
            if (ObjectDB.instance != null)
                foreach (var prefab in ObjectDB.instance.m_items)
                {
                    var shared = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
                    if (shared != null && shared.m_maxStackSize == 1 && !string.IsNullOrEmpty(shared.m_name)) files.Kept.Add(shared.m_name.TrimStart('$'));
                }
            foreach (var file in settings.Localizations)
            {
                if (file == null) continue;
                using (var reader = new StringReader(file.text))
                {
                    string[] columns = reader.ReadLine().Split(',');
                    for (int column = 1; column < columns.Length; column++)
                        files.Languages.Add(columns[column].Trim().Trim('"'));
                    int englishColumn = Array.FindIndex(columns, name => name.Trim().Trim('"') == "English");
                    var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in ReadCsv(Localization.instance, reader))
                    {
                        if (englishColumn >= 0 && row.Count > englishColumn && row[0].Length != 0 && !row[0].StartsWith("//"))
                            files.English[row[0]] = row[englishColumn].Trim();
                        if (!files.Keeps(row[0]) || !found.Add(row[0])) continue;
                        if (!files.Rows.TryGetValue(row[0], out var cells)) files.Rows[row[0]] = cells = new List<KeyValuePair<string, string>>();
                        for (int column = 1; column < columns.Length && column < row.Count; column++)
                            if (!string.IsNullOrWhiteSpace(row[column]))
                                cells.Add(new KeyValuePair<string, string>(columns[column].Trim().Trim('"'), row[column].Trim()));
                    }
                }
            }
            return gameFiles = files;
        }

        // Built on first use, in play, after every mod has added its words.
        private static Dictionary<string, string> Words()
        {
            if (words != null) return words;
            var english = Read().English;
            // In Jotunn's order, so a later mod's word replaces an earlier one's, as in the game.
            foreach (var mod in ((Dictionary<string, CustomLocalization>)ModLocalizations.GetValue(LocalizationManager.Instance)).Values)
                foreach (var word in mod.GetTranslations("English"))
                    english[word.Key] = word.Value;
            return words = english;
        }
    }
}
