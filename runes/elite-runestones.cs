using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class EliteRunestones
    {
        private static readonly Type Settings = typeof(LootRoller).Assembly.GetType("EpicLoot.Config.ELConfig", true);
        private static readonly ConfigEntry<bool> Enabled = (ConfigEntry<bool>)AccessTools.Field(Settings, "EliteRunestoneDrops").GetValue(null);
        private static readonly ConfigEntry<float> Chance = (ConfigEntry<float>)AccessTools.Field(Settings, "EliteRunestoneDropChance").GetValue(null);
        private static readonly ConfigEntry<float> GlobalRate = (ConfigEntry<float>)AccessTools.Field(Settings, "GlobalDropRateModifier").GetValue(null);
        private static readonly int CorpseEligibility = (Plugin.Id + ".elite-runestone-eligible").GetStableHashCode();
        private static readonly AccessTools.FieldRef<CharacterDrop, bool> DropsEnabled = AccessTools.FieldRefAccess<CharacterDrop, bool>("m_dropsEnabled");
        [ThreadStatic] private static bool allowed;

        // Aliases resolve to these actual loaded table objects. Creature stars and sorter suggestions are irrelevant.
        private static readonly Dictionary<string, string> Tiers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Tier3EliteMob"] = "Magic", ["BlackForest_Elite"] = "Magic",
            ["Tier4EliteMob"] = "Rare", ["Swamp_Elite"] = "Rare",
            ["Tier5EliteMob"] = "Epic", ["Mountain_Elite"] = "Epic",
            ["Tier6EliteMob"] = "Legendary", ["Plains_Elite"] = "Legendary",
            ["Tier7EliteMob"] = "Legendary", ["Mistlands_Elite"] = "Legendary",
            ["Tier8EliteMob"] = "Mythic", ["AshLands_Elite"] = "Mythic",
            ["Tier9EliteMob"] = "Ancient"
        };

        private static readonly Dictionary<string, string> Overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Moose"] = "", ["Lox"] = "", ["JotunWitch"] = "Ancient"
        };

        internal static void Initialize(IntegrationPatches patches)
        {
            patches.Patch(AccessTools.Method(typeof(EliteRunestoneTables), "AddTables"),
                prefix: new HarmonyMethod(typeof(EliteRunestones), nameof(ReplaceNativeTables)));
            CreatureLootScope.Patch(patches, typeof(EliteRunestones));
            // This integration shares the star-reward transaction: refresh only after every replacement hook is installed.
            patches.Changes.Add(EliteRunestoneTables.Refresh);
            patches.Undo.Add(EliteRunestoneTables.Refresh);
        }

        private static bool ReplaceNativeTables() => false;

        private static bool CanDrop(CharacterDrop drop)
        {
            var character = drop ? drop.GetComponent<Character>() : null;
            var view = drop ? drop.GetComponent<ZNetView>() : null;
            return character && !character.IsPlayer() && !character.IsTamed() && DropsEnabled(drop) && view && view.IsValid() && view.IsOwner();
        }

        private static void BeginDeath(CharacterDrop __0, out bool __state) { __state = allowed; allowed = CanDrop(__0); }

        private static void RememberCorpse(Ragdoll __0, CharacterDrop __1)
        {
            var view = __0 ? __0.GetComponent<ZNetView>() : null;
            if (view && view.IsValid() && view.IsOwner()) view.GetZDO().Set(CorpseEligibility, __0.m_dropItems && CanDrop(__1));
        }

        private static void BeginCorpse(Ragdoll __0, out bool __state)
        {
            __state = allowed;
            var view = __0 ? __0.GetComponent<ZNetView>() : null;
            allowed = view && view.IsValid() && view.IsOwner() && view.GetZDO().GetBool(CorpseEligibility);
            if (allowed) view.GetZDO().Set(CorpseEligibility, false);
        }

        private static void EndDeath(bool __state) => allowed = __state;

        internal static string Tier(IEnumerable<LootTable> tables, string creature)
        {
            if (Overrides.TryGetValue(creature, out var special)) return special.Length == 0 ? null : special;
            foreach (var table in tables)
                if (table?.Object != null && Tiers.TryGetValue(table.Object, out var rarity)) return rarity;
            return null;
        }

        internal static float Probability(float chance, float globalRate)
        {
            chance = Mathf.Clamp01(chance);
            var weights = LootScalingMath.ApplyDropRate(new List<KeyValuePair<int, float>>
            {
                new KeyValuePair<int, float>(0, (1f - chance) * 100f), new KeyValuePair<int, float>(1, chance * 100f)
            }, Mathf.Clamp(globalRate, 0f, 4f));
            float total = weights.Sum(pair => pair.Value);
            return total > 0f ? weights.Where(pair => pair.Key == 1).Sum(pair => pair.Value) / total : 0f;
        }

        internal static bool Succeeds(float probability, float draw) => probability >= 1f || draw < probability;
        private static bool Roll(float probability) => Succeeds(probability, UnityEngine.Random.value);
        private static int NorthernChoice() => UnityEngine.Random.Range(0, 2);

        internal static void Append(List<LootTable> tables, string creature, Vector3 position, List<GameObject> output)
        {
            if (!allowed || !Enabled.Value) return;
            var tier = Tier(tables, creature);
            if (tier == null || !Roll(Probability(Chance.Value, GlobalRate.Value))) return;
            var rarity = tier == "Ancient" ? (NorthernChoice() == 0 ? "Mythic" : "Ancient") : tier;
            string name = "Runestone" + rarity;
            var prefab = ObjectDB.instance.GetItemPrefab(name);
            if (!prefab) throw new InvalidOperationException("Elite runestone prefab is unavailable: " + name);
            LootOutput.Create(LootOutput.ForObjects(output), prefab, position, out _);
        }
    }
}
