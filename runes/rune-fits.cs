using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EpicLoot;
using EpicLoot.Crafting;
using EpicLoot.GatedItemType;
using EpicLoot.ShardStones;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Which gear a rune fits: every base item players can craft or loot, tested as a socket or the Runes tab tests it on a
    // fresh item of the best rarity the enchantment allows, grouped into the kinds players know. What depends on the target
    // item itself, such as a free socket or a conflicting enchantment, is left to the socket's own refusal.
    [HarmonyPatch]
    internal static class RuneFits
    {
        private static readonly Regex Tags = new Regex("<[^>]*>");
        private static readonly Func<string, float, object[]> DisplayArguments =
            AccessTools.MethodDelegate<Func<string, float, object[]>>(AccessTools.Method(typeof(MagicItem), "GetDisplayArgs"));
        private static readonly Func<string, object[], string> FormatText =
            AccessTools.MethodDelegate<Func<string, object[], string>>(AccessTools.Method(typeof(MagicItem), "FormatDisplayText"));

        private static readonly string[] Weapons = { "swords", "knives", "clubs", "sledges", "axes", "battleaxes", "fist weapons", "spears", "polearms", "staves", "bows", "crossbows" };
        private static readonly string[] Armor = { "helmets", "chest armor", "leg armor", "capes" };
        private static readonly string[] Kinds = Weapons.Concat(new[] { "shields", "pickaxes", "tools", "torches" }).Concat(Armor).Concat(new[] { "utility items", "trinkets" }).ToArray();

        private static readonly Dictionary<string, Result> Results = new Dictionary<string, Result>();
        private static List<ItemDrop.ItemData> candidates;

        internal sealed class Result
        {
            internal string Gear;
            internal List<string> Rules = new List<string>();
            internal bool Etchable;
            internal List<string> Details = new List<string>();
        }

        // The server's EpicLoot configs replace the local ones after joining.
        [HarmonyPostfix, HarmonyPatch(typeof(MagicItemEffectDefinitions), nameof(MagicItemEffectDefinitions.Initialize))]
        private static void EffectsChanged() => Results.Clear();

        [HarmonyPostfix, HarmonyPatch(typeof(GatedItemTypeHelper), nameof(GatedItemTypeHelper.Initialize))]
        private static void ItemsChanged() { Results.Clear(); candidates = null; }

        internal static Result For(string effectType)
        {
            if (!ObjectDB.instance || !MagicItemEffectDefinitions.TryGet(effectType, out var definition)) return null;
            if (Results.TryGetValue(effectType, out var result)) return result;
            return Results[effectType] = Compute(effectType, definition);
        }

        private static Result Compute(string effectType, MagicItemEffectDefinition definition)
        {
            var requirements = definition.Requirements ?? new MagicItemEffectRequirements();
            var result = new Result { Etchable = !requirements.NoRoll && !EnchantCostsHelper.EffectIsDeprecated(definition) };
            var rarities = ((ItemRarity[])Enum.GetValues(typeof(ItemRarity))).Where(rarity =>
                (requirements.AllowedRarities == null || requirements.AllowedRarities.Count == 0 || requirements.AllowedRarities.Contains(rarity)) &&
                (requirements.ExcludedRarities == null || !requirements.ExcludedRarities.Contains(rarity))).ToList();
            if (rarities.Count == 0) { result.Gear = "no gear"; return result; }
            var needed = requirements.MustHaveEffectTypes ?? new List<string>();

            bool Fits(MagicItemEffectRequirements checks, ItemDrop.ItemData item, bool withPrerequisites)
            {
                if (!(ShardSocketManager.RuneSocketingEnabled && GatedItemTypeHelper.IsSocketable(item)) && !result.Etchable) return false;
                var probe = new MagicItem { Rarity = rarities.Last() };
                if (withPrerequisites) probe.Effects.AddRange(needed.Select(type => new MagicItemEffect(type, 0f)));
                return checks.CheckRequirements(item, probe, effectType, checklootroll: false, checkaugmentroll: false, checkruneroll: true) &&
                    EnchantmentApplicability.DeadReason(item, probe, effectType) == null;
            }

            // A property the gear must have reads better as one rule than as every item of each kind that lacks it. Items it
            // alone keeps out are left out of the kind's exceptions.
            var stated = PropertyRules.Where(rule => requirements.GetType().GetField(rule.Key).GetValue(requirements) as bool? == true).ToList();
            var loose = Clone(requirements);
            foreach (var rule in stated) loose.GetType().GetField(rule.Key).SetValue(loose, null);
            var fitting = new Dictionary<string, List<string>>();
            var missing = new Dictionary<string, List<string>>();
            var plain = new HashSet<string>();
            bool ruled = false;
            foreach (var item in Candidates())
            {
                string kind = Kind(item);
                if (kind == null) continue;
                string name = Localization.instance.Localize(item.m_shared.m_name);
                bool fits = Fits(requirements, item, true);
                if (fits) fitting.GetOrAdd(kind).Add(name);
                else if (stated.Count > 0 && Fits(loose, item, true)) { ruled = true; fitting.GetOrAdd(kind); }
                else missing.GetOrAdd(kind).Add(name);
                if (fits && needed.Count > 0 && Fits(requirements, item, false)) plain.Add(kind);
            }
            foreach (var kind in fitting.Keys.Where(kind => fitting[kind].Count == 0).ToList()) fitting.Remove(kind);
            if (ruled) result.Rules.AddRange(stated.Select(rule => rule.Value));

            bool Full(string kind) => fitting.ContainsKey(kind) && !missing.ContainsKey(kind);
            var parts = new List<string>();
            if (Kinds.All(Full)) parts.Add("any gear");
            else
                foreach (var kind in Kinds.Where(fitting.ContainsKey))
                {
                    // A whole set of weapons or armor reads as one word, placed where its first kind would be.
                    var group = Weapons.Contains(kind) ? Weapons : Armor.Contains(kind) ? Armor : null;
                    if (group != null && group.All(Full)) { if (kind == group[0]) parts.Add(group == Weapons ? "weapons" : "armor"); continue; }
                    parts.Add(Full(kind) ? kind : "some " + kind);
                }
            result.Gear = List(parts, "and") ?? "no gear";

            // Which items of a partly fitting kind, named by the shorter side.
            foreach (var kind in Kinds.Where(kind => fitting.ContainsKey(kind) && missing.ContainsKey(kind)))
            {
                var yes = fitting[kind]; var no = missing[kind];
                string label = char.ToUpperInvariant(kind[0]) + kind.Substring(1);
                result.Details.Add(no.Count <= yes.Count ? label + ": not " + List(no, "or") + "." : label + ": only " + List(yes, "and") + ".");
            }

            var all = (ItemRarity[])Enum.GetValues(typeof(ItemRarity));
            string Rarity(ItemRarity rarity) => Localization.instance.Localize(EpicLoot.EpicLoot.GetRarityDisplayName(rarity));
            // A cap at the top keeps the rune off better gear as surely as a floor keeps it off worse.
            if (rarities.Count < all.Length)
                result.Rules.Add(rarities.SequenceEqual(all.SkipWhile(rarity => rarity != rarities[0]))
                    ? "Only on " + Rarity(rarities[0]) + " or better gear."
                    : "Only on " + List(rarities.Select(Rarity).ToList(), "or") + " gear.");
            if (needed.Count > 0)
            {
                var exempt = Kinds.Where(plain.Contains).ToList();
                result.Rules.Add("Only on gear that also has " + List(needed.Select(EffectName).ToList(), "and") +
                    (exempt.Count > 0 ? ", or on " + List(exempt, "and") : "") + ".");
            }
            return result;
        }

        private static readonly Dictionary<string, string> PropertyRules = new Dictionary<string, string>
        {
            [nameof(MagicItemEffectRequirements.ItemHasPhysicalDamage)] = "Only on gear that deals physical damage.",
            [nameof(MagicItemEffectRequirements.ItemHasElementalDamage)] = "Only on gear that deals elemental damage.",
            [nameof(MagicItemEffectRequirements.ItemHasChopDamage)] = "Only on gear that chops wood.",
            [nameof(MagicItemEffectRequirements.ItemUsesDurability)] = "Only on gear that wears out.",
            [nameof(MagicItemEffectRequirements.ItemHasNegativeMovementSpeedModifier)] = "Only on gear that slows you down.",
            [nameof(MagicItemEffectRequirements.ItemHasBlockPower)] = "Only on gear that can block.",
            [nameof(MagicItemEffectRequirements.ItemHasParryPower)] = "Only on gear that can parry.",
            [nameof(MagicItemEffectRequirements.ItemHasArmor)] = "Only on gear with armor.",
            [nameof(MagicItemEffectRequirements.ItemHasBackstabBonus)] = "Only on gear with a backstab bonus.",
            [nameof(MagicItemEffectRequirements.ItemUsesStaminaOnAttack)] = "Only on gear whose attacks cost stamina.",
            [nameof(MagicItemEffectRequirements.ItemUsesEitrOnAttack)] = "Only on gear whose attacks cost eitr.",
            [nameof(MagicItemEffectRequirements.ItemUsesHealthOnAttack)] = "Only on gear whose attacks cost health.",
            [nameof(MagicItemEffectRequirements.ItemUsesDrawStaminaOnAttack)] = "Only on gear that costs stamina to draw.",
        };

        private static MagicItemEffectRequirements Clone(MagicItemEffectRequirements requirements) =>
            Newtonsoft.Json.JsonConvert.DeserializeObject<MagicItemEffectRequirements>(Newtonsoft.Json.JsonConvert.SerializeObject(requirements),
                new Newtonsoft.Json.JsonSerializerSettings { ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace });

        private static string List(List<string> items, string conjunction) => items.Count == 0 ? null : items.Count == 1 ? items[0] :
            string.Join(", ", items.Take(items.Count - 1)) + " " + conjunction + " " + items.Last();

        private static List<string> GetOrAdd(this Dictionary<string, List<string>> map, string key) =>
            map.TryGetValue(key, out var list) ? list : map[key] = new List<string>();

        private static string EffectName(string type) => MagicItemEffectDefinitions.TryGet(type, out var definition)
            ? Tags.Replace(FormatText(definition.DisplayText, DisplayArguments(type, 0f)), "").Trim() : type;

        // Base items players can craft, loot or buy that can be magic; developer and creature items are neither.
        private static IEnumerable<ItemDrop.ItemData> Candidates()
        {
            if (candidates != null) return candidates;
            var names = new HashSet<string>(GatedItemTypeHelper.AllItemsWithDetails.Keys);
            foreach (var recipe in ObjectDB.instance.m_recipes)
                if (recipe && recipe.m_enabled && recipe.m_item) names.Add(recipe.m_item.gameObject.name);
            candidates = new List<ItemDrop.ItemData>();
            foreach (var name in names)
            {
                var prefab = ObjectDB.instance.GetItemPrefab(name);
                var drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
                if (!drop) continue;
                var item = drop.m_itemData.Clone();
                item.m_dropPrefab = prefab;
                if (EpicLoot.EpicLoot.CanBeMagicItem(item)) candidates.Add(item);
            }
            return candidates;
        }

        private static string Kind(ItemDrop.ItemData item)
        {
            switch (ItemTypeClassifier.GetItemInfoType(item))
            {
                case "Swords": return "swords";
                case "Knives": return "knives";
                case "Clubs": return "clubs";
                case "Sledges": return "sledges";
                case "Axes": return "axes";
                case "TwoHandAxes": return "battleaxes";
                case "Fists": return "fist weapons";
                case "Spears": return "spears";
                case "Polearms": return "polearms";
                case "Staffs": return "staves";
                case "Bows": return item.m_shared.m_skillType == Skills.SkillType.Crossbows ? "crossbows" : "bows";
                case "Pickaxes": return "pickaxes";
                case "Tools": return "tools";
                case "Torches": return "torches";
                case "RoundShields": case "Bucklers": case "TowerShields": return "shields";
                case "HeadArmor": return "helmets";
                case "ChestArmor": return "chest armor";
                case "LegsArmor": return "leg armor";
                case "ShouldersArmor": return "capes";
                case "Utility": return "utility items";
                case "Trinket": return "trinkets";
                default: return null;
            }
        }
    }
}
