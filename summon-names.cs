using System.Collections.Generic;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The game's text for the Spirit Caller animals is empty in every language, so their health bars and hover text showed
    // no name. Each word that is missing or empty after the game and every mod have loaded a language gets an English name;
    // a real translation and a name a player gave the animal still show. A summon whose AI is alerted reads Alerted
    // instead of the Frightened the game shows for an animal being tamed. A summon keeps the name it has: Shift + E opens no
    // rename box and its hover has no Rename row.
    internal static class SummonNames
    {
        private const string Alerted = "epiclootprogressionfix_summon_alerted";
        internal const string Ride = "epiclootprogressionfix_summon_ride";
        private static readonly Dictionary<string, string> Fallbacks = new Dictionary<string, string>
        {
            ["spiritcaller_bjorn"] = "Spirit Bear",
            ["spiritcaller_moose"] = "Spirit Moose",
            ["spiritcaller_wolf"] = "Spirit Wolf",
            ["spiritcaller_boar"] = "Spirit Boar",
            [Alerted] = "Alerted",
            [Ride] = "Ride",
        };
        private static readonly AccessTools.FieldRef<Localization, Dictionary<string, string>> Words =
            AccessTools.FieldRefAccess<Localization, Dictionary<string, string>>("m_translations");
        private static readonly AccessTools.FieldRef<Localization, LRUCache<string>> Cache =
            AccessTools.FieldRefAccess<Localization, LRUCache<string>>("m_cache");
        private static readonly AccessTools.FieldRef<Localization> Current =
            AccessTools.StaticFieldRefAccess<Localization>(AccessTools.Field(typeof(Localization), "m_instance"));

        internal static void Initialize(IntegrationPatches harmony)
        {
            // Jotunn adds mods' words after the game's when a language loads, so this runs after it.
            harmony.Patch(AccessTools.Method(typeof(Localization), nameof(Localization.SetupLanguage)),
                postfix: new HarmonyMethod(typeof(SummonNames), nameof(FillBlanks)) { priority = Priority.Last, after = new[] { Jotunn.Main.ModGuid } });
            harmony.Patch(AccessTools.Method(typeof(Tameable), nameof(Tameable.GetStatusString)),
                postfix: new HarmonyMethod(typeof(SummonNames), nameof(AlertedSummon)));
            harmony.Patch(AccessTools.Method(typeof(Tameable), nameof(Tameable.Interact)),
                prefix: new HarmonyMethod(typeof(SummonNames), nameof(NoRenaming)));
            harmony.Patch(AccessTools.Method(typeof(Tameable), nameof(Tameable.GetHoverText)),
                postfix: new HarmonyMethod(typeof(SummonNames), nameof(NoRenameRow)));
            // The game usually sets up its language before this mod loads.
            if (Current() != null) FillBlanks(Current());
        }

        private static void FillBlanks(Localization __instance)
        {
            var words = Words(__instance);
            bool filled = false;
            foreach (var fallback in Fallbacks)
                if (!words.TryGetValue(fallback.Key, out string text) || string.IsNullOrWhiteSpace(text))
                {
                    words[fallback.Key] = fallback.Value;
                    filled = true;
                }
            // Text already put together from an empty name stays in the game's cache until it is cleared.
            if (filled) Cache(__instance).EvictAll();
        }

        private static void AlertedSummon(Character ___m_character, ref string __result)
        {
            if (__result == "$hud_tamefrightened" && SummonOwnership.IsPlayerSummon(___m_character)) __result = "$" + Alerted;
        }

        private static bool NoRenaming(Character ___m_character, bool alt, ref bool __result)
        {
            if (!alt || !SummonOwnership.IsPlayerSummon(___m_character)) return true;
            __result = false;
            return false;
        }

        // The row is the last line on keyboard and gamepad alike, whatever its key labels read.
        private static void NoRenameRow(Character ___m_character, ref string __result)
        {
            int row = __result.LastIndexOf('\n');
            if (row >= 0 && __result.Substring(row).Contains(Localization.instance.Localize("$hud_rename")) && SummonOwnership.IsPlayerSummon(___m_character))
                __result = __result.Substring(0, row);
        }
    }
}
