using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using EpicLoot;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class EnglishItemNames
    {
        private static readonly TextInfo EnglishCase = CultureInfo.GetCultureInfo("en-US").TextInfo;
        private static readonly MethodInfo Localize = AccessTools.Method(typeof(Localization), "Localize", new[] { typeof(string) });
        private sealed class NameCache
        {
            internal string SavedName, BaseName, EnglishName;
            internal ItemRarity Rarity;
        }
        private static readonly ConditionalWeakTable<ItemDrop.ItemData, NameCache> Names = new ConditionalWeakTable<ItemDrop.ItemData, NameCache>();
        private static readonly Regex BrokenClubsPrefix = new Regex(@"\A(?:\$mod_epicloot_me_addclubskill_prefix1|\[\$?mod_epicloot_me_addclubskill_prefix1\])(?=\s|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // How many times each EpicLoot name builder calls Localize in 0.14.13.
        private static readonly Dictionary<string, int> NameBuilders = new Dictionary<string, int>
        {
            { "GetNameForItem", 2 }, { "GetPrefix", 1 }, { "GetSuffix", 1 }, { "BuildEpicName", 1 }, { "GetLegendaryName", 1 }, { "GetTopTierName", 1 },
        };

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(EnglishItemNames);
            // EpicLoot saves these names as literal strings shared with other players, so the creator's language must not leak.
            foreach (string name in NameBuilders.Keys)
                harmony.Patch(AccessTools.Method(typeof(MagicItemNames), name), transpiler: new HarmonyMethod(self, nameof(UseEnglish)));
            harmony.Patch(AccessTools.Method(typeof(MagicItemNames), "TranslateAndCapitalize"),
                prefix: new HarmonyMethod(self, nameof(CapitalizeEnglish)));
            harmony.Patch(AccessTools.Method(typeof(ItemDataExtensions), nameof(ItemDataExtensions.GetDisplayName)),
                postfix: new HarmonyMethod(self, nameof(DisplayLegacyName)));
        }

        private static IEnumerable<CodeInstruction> UseEnglish(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Transpilers.CallInstead(instructions, x => x.Calls(Localize), AccessTools.Method(typeof(EnglishItemNames), nameof(NameText)),
                NameBuilders[__originalMethod.Name], "EpicLoot's item name builder changed: " + __originalMethod.Name);

        private static string NameText(Localization unused, string text)
        {
            EnglishText.TryTranslate(text, out string english);
            return english;
        }

        private static bool CapitalizeEnglish(string baseName, ref string __result)
        {
            __result = EnglishCase.ToTitleCase(NameText(null, baseName));
            return false;
        }

        private static string InLanguage(Dictionary<string, string> translations, string language) =>
            translations.TryGetValue(language, out string text) ? text : translations["English"];

        private static string Pattern(string format, int baseIndex, string baseName)
        {
            string pattern = Regex.Escape(format);
            for (int index = 0; index < 3; index++)
                pattern = pattern.Replace(Regex.Escape("{" + index + "}"), index == baseIndex
                    ? Regex.Escape(baseName) : "(?<part" + index + ">.*?)");
            return "\\A" + pattern + "\\z";
        }

        private static string Rewrap(string part, string formatToken, string language)
        {
            if (part.Length == 0) return part;
            var formats = EnglishText.Translations(formatToken);
            var match = Regex.Match(part, Pattern(InLanguage(formats, language), -1, ""),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
            return match.Success ? string.Format(formats["English"], match.Groups["part0"].Value) : part;
        }

        private static void DisplayLegacyName(ItemDrop.ItemData itemData, ref string __result)
        {
            // Another mod may have renamed the item; then there is no saved name in front to replace.
            if (!global::EpicLoot.EpicLoot.CanBeMagicItem(itemData) || !itemData.IsMagic(out var magicItem) ||
                string.IsNullOrEmpty(magicItem.DisplayName) || !string.IsNullOrEmpty(magicItem.LegendaryID) ||
                __result == null || !__result.StartsWith(magicItem.DisplayName, StringComparison.Ordinal)) return;
            // Inventory labels refresh frequently. Recognize an unchanged saved name once, without retaining old items.
            var cached = Names.GetValue(itemData, unused => new NameCache());
            if (cached.SavedName != magicItem.DisplayName || cached.BaseName != itemData.m_shared.m_name || cached.Rarity != magicItem.Rarity)
            {
                cached.EnglishName = BrokenClubsPrefix.Replace(LegacyName(itemData, magicItem),
                    unused => NameText(null, "$mod_epicloot_me_addclubsskill_prefix1"));
                cached.SavedName = magicItem.DisplayName;
                cached.BaseName = itemData.m_shared.m_name;
                cached.Rarity = magicItem.Rarity;
            }
            // Keep EpicLoot's separate equipment-stat suffix and all saved item data. No reroll or save migration.
            __result = cached.EnglishName + __result.Substring(magicItem.DisplayName.Length);
        }

        private static string LegacyName(ItemDrop.ItemData itemData, MagicItem magicItem)
        {
            string token;
            int baseIndex = 0;
            switch (magicItem.Rarity)
            {
                case ItemRarity.Magic: token = "$mod_epicloot_basicmagicnameformat"; break;
                case ItemRarity.Rare: token = "$mod_epicloot_fullnameformat"; baseIndex = 1; break;
                case ItemRarity.Epic: token = "$mod_epicloot_epicnameformat"; baseIndex = 1; break;
                case ItemRarity.Legendary: token = "$mod_epicloot_basiclegendarynameformat"; break;
                case ItemRarity.Mythic: token = "$mod_epicloot_basicmythicnameformat"; break;
                case ItemRarity.Ancient: token = "$mod_epicloot_basicancientnameformat"; break;
                default: return magicItem.DisplayName;
            }
            string baseName = EnglishCase.ToTitleCase(NameText(null, itemData.m_shared.m_name));
            string englishFormat = NameText(null, token);
            if (baseIndex == 0 && magicItem.DisplayName == string.Format(englishFormat, baseName)) return magicItem.DisplayName;
            var bases = EnglishText.Translations(itemData.m_shared.m_name);
            var formats = EnglishText.Translations(token);
            foreach (var translatedBase in bases)
            {
                var match = Regex.Match(magicItem.DisplayName,
                    Pattern(InLanguage(formats, translatedBase.Key), baseIndex, translatedBase.Value),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
                if (!match.Success) continue;
                var parts = new object[] { match.Groups["part0"].Value, baseName, match.Groups["part2"].Value };
                parts[baseIndex] = baseName;
                if (magicItem.Rarity == ItemRarity.Rare)
                {
                    parts[0] = Rewrap((string)parts[0], "$mod_epicloot_prefixformat", translatedBase.Key);
                    parts[2] = Rewrap((string)parts[2], "$mod_epicloot_suffixformat", translatedBase.Key);
                }
                return string.Format(englishFormat, parts);
            }
            return magicItem.DisplayName;
        }
    }
}
