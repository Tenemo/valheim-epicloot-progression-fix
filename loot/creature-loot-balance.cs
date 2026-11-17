using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class CreatureLootBalance
    {
        [ThreadStatic] private static string creatureName;
        [ThreadStatic] private static bool boss;
        private static Dictionary<string, ItemRarity> ceilings;
        internal static bool Ordinary => creatureName != null;

        internal static void Initialize(IntegrationPatches harmony)
        {
            // A malformed list stops startup; one that later arrives from the server leaves the current ceilings in effect.
            ceilings = ReadCeilings(Plugin.CreatureRarityCeilings.Value);
            Plugin.CreatureRarityCeilings.SettingChanged += (sender, arguments) =>
            {
                try { ceilings = ReadCeilings(Plugin.CreatureRarityCeilings.Value); }
                catch (FormatException error) { Debug.LogError(error.Message + " The previous rarity ceilings stay in effect."); }
            };
            var helper = typeof(LootRoller).Assembly.GetType("EpicLoot.EpicLootDropsHelper");
            var self = typeof(CreatureLootBalance);
            harmony.Patch(AccessTools.Method(helper, "OnCharacterDeath", new[] { typeof(string), typeof(int), typeof(Vector3), typeof(bool) }),
                prefix: new HarmonyMethod(self, nameof(BeginDeath)), finalizer: new HarmonyMethod(self, nameof(EndDeath)));
            harmony.Patch(AccessTools.Method(typeof(LootRoller), nameof(LootRoller.ResolveLootDrop),
                new[] { typeof(LootDrop), typeof(float), typeof(bool), typeof(LootScalingContext) }),
                prefix: new HarmonyMethod(self, nameof(SelectCreatureStonePool)));
            harmony.Patch(AccessTools.Method(typeof(LootRoller), nameof(LootRoller.GetRarityWeights)),
                postfix: new HarmonyMethod(self, nameof(CapRarity)) { priority = Priority.Last });
            harmony.Patch(AccessTools.GetDeclaredMethods(typeof(LootRoller)).Single(method => method.Name == "RollLootTableInternal" &&
                method.GetParameters()[0].ParameterType == typeof(LootTable)), transpiler: new HarmonyMethod(self, nameof(Quantity)));
        }

        private static void BeginDeath(string __0, out (string Creature, bool Boss) __state)
        {
            __state = (creatureName, boss);
            var prefab = ZNetScene.instance.GetPrefab(__0)?.GetComponent<Character>();
            // Bounties can mark an ordinary creature as a boss; its species still determines the reward.
            creatureName = prefab && !prefab.IsBoss() ? __0 : null;
            boss = prefab && prefab.IsBoss() && prefab.BossOrder() > 0;
        }

        private static void EndDeath((string Creature, bool Boss) __state) { creatureName = __state.Creature; boss = __state.Boss; }

        // Apply the pack policy after native star/class bonuses, so newly generated tables cannot bypass it.
        private static IEnumerable<CodeInstruction> Quantity(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var native = AccessTools.Method(typeof(LootScalingMath), nameof(LootScalingMath.AddBonusDrops));
            int matches = 0;
            for (int index = 0; index < code.Count; index++)
            {
                if (!code[index].Calls(native)) continue;
                code.Insert(index, new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code[index]));
                code[++index].operand = AccessTools.Method(typeof(CreatureLootBalance), nameof(DropCount));
                matches++;
            }
            if (matches != 1) throw new InvalidOperationException("EpicLoot's final drop quantity changed.");
            return code;
        }

        private static int DropCount(int count, float bonus, float roll, int maximum, LootTable table)
        {
            count = LootScalingMath.AddBonusDrops(count, bonus, roll, maximum);
            if (count <= 0) return count;
            if (Ordinary && Plugin.OrdinaryMagicLootShare.Value < 1f)
                return UnityEngine.Random.value < Plugin.OrdinaryMagicLootShare.Value ? count : 0;
            if (!boss || Plugin.BossMagicLootShare.Value >= 1f ||
                !table.LeveledLoot.SelectMany(level => level.Loot ?? Array.Empty<LootDrop>()).Any(BountyLoot.EquipmentBranch)) return count;
            float scaled = count * Plugin.BossMagicLootShare.Value;
            int whole = Mathf.FloorToInt(scaled);
            return Math.Max(1, whole + (UnityEngine.Random.value < scaled - whole ? 1 : 0));
        }

        // Comma-separated Prefab:Rarity pairs, such as "Moose:Mythic, Elaking:Legendary".
        internal static Dictionary<string, ItemRarity> ReadCeilings(string setting)
        {
            var result = new Dictionary<string, ItemRarity>();
            foreach (string pair in setting.Split(','))
            {
                if (pair.Trim().Length == 0) continue;
                var parts = pair.Split(':');
                string creature = parts[0].Trim(), rarity = parts.Length == 2 ? parts[1].Trim() : "";
                if (creature.Length == 0 || Array.IndexOf(Enum.GetNames(typeof(ItemRarity)), rarity) < 0 || result.ContainsKey(creature))
                    throw new FormatException($"Creature loot rarity ceilings: \"{pair.Trim()}\" is not a Prefab:Rarity pair for a new creature.");
                result[creature] = (ItemRarity)Enum.Parse(typeof(ItemRarity), rarity);
            }
            return result;
        }

        private static void SelectCreatureStonePool(ref LootDrop __0)
        {
            if (creatureName == null) return;
            string pool;
            switch (__0.Item)
            {
                case "ShardStone_AshLands": pool = "CreatureShardStone_AshLands"; break;
                case "ShardStone_DeepNorth": pool = "CreatureShardStone_DeepNorth"; break;
                default: return;
            }
            // The pack supplies these pools. Standalone installations retain their own biome presets.
            if (!LootRoller.ItemSets.ContainsKey(pool)) return;
            __0 = new LootDrop { Item = pool, Weight = __0.Weight, WeightPerStar = __0.WeightPerStar,
                Rarity = __0.Rarity, RarityItems = __0.RarityItems, ExtraData = __0.ExtraData };
        }

        private static void CapRarity(Dictionary<ItemRarity, float> __result)
        {
            if (creatureName == null || !ceilings.TryGetValue(creatureName, out var maximum)) return;
            // Clamp the final selection after stars, Hoarding, Luck and the existing equipment reduction.
            // RarityItems uses this same selection for stone colors, and unidentified/material drops use it too.
            float excess = 0f;
            foreach (var rarity in Rarities.All)
            {
                if (rarity <= maximum) continue;
                excess += __result[rarity];
                __result[rarity] = 0f;
            }
            __result[maximum] += excess;
        }
    }
}
