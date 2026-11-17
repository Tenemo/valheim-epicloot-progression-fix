using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using EpicLoot;
using EpicLoot.Biomes;
using EpicLoot.GatedItemType;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class OriginTiers
    {
        private sealed class Tier { internal int? Value; }
        private static readonly ConditionalWeakTable<LootDrop, Tier> Sources = new ConditionalWeakTable<LootDrop, Tier>();
        [ThreadStatic] internal static int? Current;
        private static readonly Regex TierName = new Regex(@"^Tier(\d+)(Everything|Weapons|Armor|Shields|Tools|Trinkets)$");

        internal static int? Get(LootDrop drop) => drop != null && Sources.TryGetValue(drop, out var tier) ? tier.Value : null;
        internal static void Remember(LootDrop drop)
        {
            if (drop != null) Sources.GetValue(drop, _ => new Tier()).Value = Current;
        }

        internal static bool FindSet(Dictionary<string, LootItemSet> sets, string name, out LootItemSet set)
        {
            bool found = sets.TryGetValue(name, out set);
            var match = TierName.Match(name);
            if (found && match.Success)
            {
                int tier = int.Parse(match.Groups[1].Value);
                Current = Current.HasValue ? Math.Min(Current.Value, tier) : tier;
            }
            return found;
        }

        internal static bool TryGetBiome(LootDrop drop, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.Meadows;
            if (!GatedItemTypeHelper.AllItemsWithDetails.TryGetValue(drop.Item, out var details) || details.RequiredBosses.Count == 0)
                return false;
            var order = GatedItemTypeHelper.BiomesInOrder;
            biome = details.RequiredBosses.Select(BiomeDataManager.GetFirstBiomeForBossKey)
                .OrderBy(order.IndexOf).First();
            int? tier = Get(drop);
            if (tier.HasValue)
            {
                var names = new[] { Heightmap.Biome.Meadows, Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest,
                    Heightmap.Biome.Swamp, Heightmap.Biome.Mountain, Heightmap.Biome.Plains,
                    Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth };
                if (tier.Value < 0 || tier.Value >= names.Length) return false;
                var ceiling = names[tier.Value];
                if (order.IndexOf(ceiling) < order.IndexOf(biome)) biome = ceiling;
            }
            if (biome == Heightmap.Biome.None) biome = Heightmap.Biome.Meadows;
            return true;
        }
    }

    [HarmonyPatch(typeof(LootRoller), nameof(LootRoller.ResolveLootDrop), new[] { typeof(LootDrop), typeof(float), typeof(bool), typeof(LootScalingContext) })]
    internal static class RecordOrigin
    {
        private static void Prefix(LootDrop lootDrop, out int? __state)
        {
            __state = OriginTiers.Current;
            OriginTiers.Current = OriginTiers.Get(lootDrop);
        }
        private static void Postfix(LootDrop __result) => OriginTiers.Remember(__result);
        private static void Finalizer(int? __state) => OriginTiers.Current = __state;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(AccessTools.Method(typeof(Dictionary<string, LootItemSet>), "TryGetValue")),
                AccessTools.Method(typeof(OriginTiers), nameof(OriginTiers.FindSet)), 1, "EpicLoot loot resolver changed; expected one item-set lookup.");
    }

    [HarmonyPatch(typeof(LootRoller), "TrySpawnUnidentified")]
    internal static class UnidentifiedTier
    {
        private static readonly Action<ItemDrop> Save = AccessTools.MethodDelegate<Action<ItemDrop>>(
            AccessTools.Method(typeof(ItemDrop), "Save", Type.EmptyTypes));

        private static bool Prefix(LootDrop lootDrop, ref Vector3 dropPoint, float luckFactor,
            object output, ref bool __result)
        {
            if (!Eligibility.CraftingMode) return true;
            __result = false;
            if (!OriginTiers.TryGetBiome(lootDrop, out var biome))
            {
                EpicLoot.EpicLoot.LogWarning($"No equipment tier for {lootDrop.Item}; using normal item resolution.");
                return false;
            }
            var rarity = LootRoller.RollItemRarity(lootDrop, luckFactor);
            string name = $"{BiomeDataManager.GetName(biome)}_{rarity}_Unidentified";
            var prefab = ObjectDB.instance.GetItemPrefab(name);
            if (prefab == null)
            {
                EpicLoot.EpicLoot.LogWarning($"Missing {name}; using normal item resolution.");
                return false;
            }
            var item = LootOutput.Create(output, prefab, dropPoint, out var drop);
            item.SaveMagicItem(new MagicItem { Rarity = rarity, IsUnidentified = true });
            if (drop) Save(drop);
            __result = true;
            return false;
        }
    }
}
