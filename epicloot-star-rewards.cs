using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class EpicLootStarRewards
    {
        private static readonly MethodInfo RollLoot = AccessTools.Method(typeof(LootRoller), nameof(LootRoller.RollLootTableAndSpawnObjects),
            new[] { typeof(List<LootTable>), typeof(int), typeof(string), typeof(Vector3), typeof(bool) });
        private static readonly int HoardingKey = (Plugin.Id + ".hoarding-loot").GetStableHashCode();
        private static MethodInfo creatureModifiers;
        [ThreadStatic] private static float rarityFactor;
        [ThreadStatic] private static bool hoarding;
        [ThreadStatic] private static bool equipmentRarity;

        internal static void Initialize(IntegrationPatches harmony)
        {
            EliteRunestones.Initialize(harmony);
            var helper = typeof(LootRoller).Assembly.GetType("EpicLoot.EpicLootDropsHelper");
            harmony.Patch(AccessTools.Method(helper, "OnCharacterDeath", new[] { typeof(string), typeof(int), typeof(Vector3), typeof(bool) }),
                transpiler: new HarmonyMethod(typeof(EpicLootStarRewards), nameof(ScaleCreatureRolls)));
            harmony.Patch(AccessTools.Method(typeof(LootRoller), nameof(LootRoller.GetRarityWeights)),
                prefix: new HarmonyMethod(typeof(EpicLootStarRewards), nameof(ScaleRarityWeights)),
                postfix: new HarmonyMethod(typeof(EpicLootStarRewards), nameof(ReduceCreatureRarity)));
            harmony.Patch(AccessTools.Method(typeof(LootRoller), nameof(LootRoller.RollItemRarity)),
                prefix: new HarmonyMethod(typeof(EpicLootStarRewards), nameof(BeginEquipmentRarity)),
                finalizer: new HarmonyMethod(typeof(EpicLootStarRewards), nameof(EndEquipmentRarity)));
            if (!StarLevelSystem.Installed) return;
            creatureModifiers = AccessTools.Method(StarLevelSystem.CompositeLazyCache, "GetCreatureModifiers", new[] { typeof(Character) });
            if (creatureModifiers == null) throw new InvalidOperationException("StarLevelSystem's creature modifiers changed.");
            // Scope the whole EpicLoot death callback, including its Lucky Loot rolls. A name and level alone do not
            // identify which creature had Hoarding. Corpse drops carry that fact on the ragdoll for its eventual owner.
            CreatureLootScope.Patch(harmony, typeof(EpicLootStarRewards));
        }

        private static bool HasHoarding(CharacterDrop drop)
        {
            var character = drop ? drop.GetComponent<Character>() : null;
            if (!character || character.IsPlayer() || character.IsTamed()) return false;
            // Bounty targets can be marked as bosses on the instance; use the prefab, as the star bonus does.
            var prefab = ZNetScene.instance.GetPrefab(global::EpicLoot.EpicLoot.GetCharacterCleanName(character))?.GetComponent<Character>();
            if (!prefab || prefab.IsBoss()) return false;
            return creatureModifiers.Invoke(null, new object[] { character }) is IDictionary modifiers && modifiers.Contains("Lootbags");
        }

        private static void BeginDeath(CharacterDrop __0, out bool __state)
        {
            __state = hoarding;
            hoarding = HasHoarding(__0);
        }

        private static void RememberCorpse(Ragdoll __0, CharacterDrop __1)
        {
            var view = __0 ? __0.GetComponent<ZNetView>() : null;
            if (view && view.IsValid()) view.GetZDO().Set(HoardingKey, HasHoarding(__1));
        }

        private static void BeginCorpse(Ragdoll __0, out bool __state)
        {
            __state = hoarding;
            var view = __0 ? __0.GetComponent<ZNetView>() : null;
            hoarding = view && view.IsValid() && view.GetZDO().GetBool(HoardingKey);
        }

        private static void EndDeath(bool __state) => hoarding = __state;

        private static IEnumerable<CodeInstruction> ScaleCreatureRolls(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(RollLoot), AccessTools.Method(typeof(EpicLootStarRewards), nameof(RollCreatureLoot)), 1,
                "EpicLoot's creature loot entry point changed; expected one loot roll.");

        private static List<GameObject> RollCreatureLoot(List<LootTable> tables, int level, string creatureName, Vector3 dropPoint, bool allowBonusRolls)
        {
            float factor = 1f;
            int lootLevel = level;
            if (level > 6 && Plugin.EpicLootStarRewardIncrease.Value > 0f)
            {
                var creature = ZNetScene.instance.GetPrefab(creatureName)?.GetComponent<Character>();
                if (creature != null && !creature.IsBoss())
                {
                    // SLS supports up to 150 stars. Invalid higher levels cannot create unbounded extra rolls.
                    factor += (Math.Min(level, 151) - 6) * Plugin.EpicLootStarRewardIncrease.Value;
                    lootLevel = 6;
                }
            }
            if (hoarding) factor *= Plugin.HoardingRewardMultiplier.Value;
            float previous = rarityFactor;
            rarityFactor = factor;
            try
            {
                var result = LootRoller.RollLootTableAndSpawnObjects(tables, lootLevel, creatureName, dropPoint, allowBonusRolls);
                float extra = factor - 1f;
                int whole = Mathf.FloorToInt(extra);
                for (int roll = 0; roll < whole; roll++)
                    result.AddRange(LootRoller.RollLootTableAndSpawnObjects(tables, lootLevel, creatureName, dropPoint, false));
                float fraction = extra - whole;
                if (fraction > 0f && UnityEngine.Random.value < fraction)
                    result.AddRange(LootRoller.RollLootTableAndSpawnObjects(tables, lootLevel, creatureName, dropPoint, false));
                BountyLoot.EnsureEquipment(tables, lootLevel, creatureName, dropPoint, result);
                if (allowBonusRolls) EliteRunestones.Append(tables, creatureName, dropPoint, result);
                return result;
            }
            finally { rarityFactor = previous; }
        }

        private static void ScaleRarityWeights(ref float[] __0)
        {
            if (__0 == null || rarityFactor <= 0f) return;
            // Modify this roll's input only. EpicLoot normalizes weights and then applies its existing Luck rule.
            __0 = (float[])__0.Clone();
            if (CreatureLootBalance.Ordinary && equipmentRarity && Plugin.TemplateHighRarityShare.Value < 1f)
            {
                float removed = 0f;
                for (int rarity = (int)ItemRarity.Legendary; rarity < __0.Length; rarity++)
                {
                    float kept = __0[rarity] * Plugin.TemplateHighRarityShare.Value;
                    removed += __0[rarity] - kept;
                    __0[rarity] = kept;
                }
                __0[(int)ItemRarity.Rare] += removed * .5f;
                __0[(int)ItemRarity.Epic] += removed * .5f;
            }
            if (rarityFactor <= 1f) return;
            for (int rarity = (int)ItemRarity.Legendary; rarity < __0.Length; rarity++) __0[rarity] *= rarityFactor;
        }

        private static void BeginEquipmentRarity(LootDrop __0, out bool __state)
        {
            __state = equipmentRarity;
            equipmentRarity = false;
            if (rarityFactor <= 0f || __0.RarityItems != null || string.IsNullOrEmpty(__0.Item)) return;
            var item = ObjectDB.instance.GetItemPrefab(__0.Item)?.GetComponent<ItemDrop>();
            equipmentRarity = item && global::EpicLoot.EpicLoot.IsAllowedMagicItemType(item.m_itemData);
        }

        private static void EndEquipmentRarity(bool __state) => equipmentRarity = __state;

        private static void ReduceCreatureRarity(Dictionary<ItemRarity, float> __result)
        {
            if (!equipmentRarity) return;
            // Preserve the total after star bonuses, Hoarding and Luck, so each final high-rarity probability is exactly
            // the configured share of its current value, including tables whose original rolls were all Legendary+.
            float share = Plugin.HighRarityEquipmentShare.Value, removed = 0f;
            foreach (var rarity in Rarities.All)
            {
                if (rarity < ItemRarity.Legendary) continue;
                float kept = __result[rarity] * share;
                removed += __result[rarity] - kept;
                __result[rarity] = kept;
            }
            __result[ItemRarity.Rare] += removed;
        }
    }
}
