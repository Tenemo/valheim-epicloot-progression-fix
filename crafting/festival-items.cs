using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The Player prefab lists Valheim's festivals, Yule, Halloween and Midsummer, each with pieces and recipes the game keeps
    // switched off except between its dates on the player's own PC. A setting per festival switches that festival's own items on
    // all year, as the game's ordinary items are, with their usual costs and stations. The date check stays, so a festival left
    // off still shows its items only in season.
    internal static class FestivalItems
    {
        private sealed class Festival
        {
            internal string Season;
            internal ConfigEntry<bool> Setting;
            // The items the setting's help names; mod-compatibility fails when an enabled festival loses one.
            internal string[] Pieces, Recipes;
            internal string Feature => Season + " items all year";
        }

        private static readonly AccessTools.FieldRef<Player, List<SeasonalItemGroup>> Groups =
            AccessTools.FieldRefAccess<Player, List<SeasonalItemGroup>>("m_seasonalItemGroups");
        private static readonly Action<Player> UpdateKnownRecipes =
            AccessTools.MethodDelegate<Action<Player>>(AccessTools.Method(typeof(Player), "UpdateKnownRecipesList"));
        private static readonly Action<Player> UpdateAvailablePieces =
            AccessTools.MethodDelegate<Action<Player>>(AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList"));
        // The game's own switch for each festival piece and recipe, as it was before this changed any.
        private static readonly Dictionary<UnityEngine.Object, bool> Native = new Dictionary<UnityEngine.Object, bool>();
        private static Festival[] festivals;

        internal static void Initialize(IntegrationPatches patches)
        {
            festivals = new[]
            {
                new Festival { Season = "Yule", Setting = Plugin.YuleAllYear,
                    Pieces = new[] { "piece_gift1", "piece_gift2", "piece_gift3", "piece_xmascrown", "piece_xmasgarland", "piece_xmastree", "piece_mistletoe" }, Recipes = new string[0] },
                new Festival { Season = "Halloween", Setting = Plugin.HalloweenAllYear, Pieces = new[] { "piece_jackoturnip" }, Recipes = new[] { "Recipe_HelmetPointyHat" } },
                new Festival { Season = "Midsummer", Setting = Plugin.MidsummerAllYear, Pieces = new[] { "piece_maypole" }, Recipes = new[] { "Recipe_HelmetMidsummerCrown" } },
            };
            // Before Awake builds the player's known recipes and build pieces.
            patches.Patch(AccessTools.Method(typeof(Player), "Awake"), prefix: new HarmonyMethod(typeof(FestivalItems), nameof(BeforePlayer)));
            foreach (var festival in festivals)
                IntegrationChanges.Defer(() => festival.Setting.SettingChanged += Refresh, () => festival.Setting.SettingChanged -= Refresh);
        }

        private static void BeforePlayer(Player __instance) => Apply(Groups(__instance));

        // A value that changes in play, as when the server's arrives after login, switches the items at once and teaches the
        // character the newly available ones, as the game does when a festival begins.
        private static void Refresh(object sender, EventArgs arguments)
        {
            var player = Player.m_localPlayer;
            var prefab = ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab("Player")?.GetComponent<Player>();
            var source = player != null ? player : prefab;
            if (source == null || !Apply(Groups(source)) || player == null) return;
            UpdateKnownRecipes(player);
            UpdateAvailablePieces(player);
        }

        // Sets every festival item's switch from the game's own value and its festival's setting, so repeating it changes
        // nothing. Returns whether any switch changed.
        private static bool Apply(List<SeasonalItemGroup> groups)
        {
            if (groups == null) return false;
            var wanted = new HashSet<UnityEngine.Object>();
            foreach (var festival in festivals)
            {
                var group = groups.FirstOrDefault(value => value != null && value.name == festival.Season);
                if (!festival.Setting.Value) { DisabledFeatures.Set(festival.Feature, null); continue; }
                var missing = group == null ? null : festival.Pieces.Where(name => !group.Pieces.Any(piece => piece != null && piece.name == name))
                    .Concat(festival.Recipes.Where(name => !group.Recipes.Any(recipe => recipe != null && recipe.name == name))).ToList();
                DisabledFeatures.Set(festival.Feature, group == null ? "the game has no " + festival.Season + " festival"
                    : missing.Count > 0 ? festival.Season + " no longer includes " + string.Join(", ", missing) : null);
                if (group == null) continue;
                wanted.UnionWith(group.Pieces.Where(piece => piece != null).Select(piece => piece.GetComponent<Piece>()).Where(piece => piece != null));
                wanted.UnionWith(group.Recipes.Where(recipe => recipe != null));
            }
            bool changed = false, recipes = false;
            foreach (var group in groups.Where(value => value != null))
            {
                foreach (var piece in group.Pieces.Where(value => value != null).Select(value => value.GetComponent<Piece>()).Where(value => value != null))
                {
                    bool enabled = NativeValue(piece, piece.m_enabled) || wanted.Contains(piece);
                    if (piece.m_enabled != enabled) { piece.m_enabled = enabled; changed = true; }
                }
                foreach (var recipe in group.Recipes.Where(value => value != null))
                {
                    bool enabled = NativeValue(recipe, recipe.m_enabled) || wanted.Contains(recipe);
                    if (recipe.m_enabled != enabled) { recipe.m_enabled = enabled; changed = recipes = true; }
                }
            }
            // The idols' descriptions list the enabled recipes they refine, such as the pointy hat's.
            if (recipes && ObjectDB.instance != null) RefinementIdols.Describe(ObjectDB.instance);
            return changed;
        }

        private static bool NativeValue(UnityEngine.Object item, bool current)
        {
            if (!Native.TryGetValue(item, out bool value)) Native[item] = value = current;
            return value;
        }
    }
}
