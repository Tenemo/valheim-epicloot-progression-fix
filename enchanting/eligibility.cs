using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx.Logging;
using EpicLoot;
using EpicLoot.GatedItemType;
using HarmonyLib;
using Rules = EpicLoot.GatedItemType.GatedItemTypeHelper;

namespace EpicLootProgressionFix
{
    internal static class Eligibility
    {
        internal static bool CraftingMode => EpicLoot.EpicLoot.GetGatedItemTypeMode() == GatedItemTypeMode.PlayerMustHaveCraftedItem;

        internal static List<string> Candidates(string item)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(item) || LootDenyList.IsDenied(item)) return result;
            if (LootRoller.LootSetContainsEntry(item))
            {
                foreach (var table in LootRoller.GetFullyResolvedLootTable(item))
                foreach (var entry in LootRoller.GetLootForLevel(table, 1).Where(x => x.Weight > 0))
                    result.AddRange(Candidates(entry.Item));
                return result.Distinct().ToList();
            }
            if (Rules.ItemsByTypeAndBoss.ContainsKey(item))
                return FromType(item, Rules.DetermineValidBosses(GatedItemTypeMode.PlayerMustHaveCraftedItem, false));
            if (!Rules.AllItemsWithDetails.TryGetValue(item, out var details))
            {
                var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(item) : null;
                var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop != null && !EpicLoot.EpicLoot.IsAllowedMagicItemType(drop.m_itemData)) result.Add(item);
                return result;
            }
            if (IsCraftingEligible(item)) return new List<string> { item };
            var tiers = Rules.DetermineValidBosses(GatedItemTypeMode.PlayerMustHaveCraftedItem, false, details.RequiredBosses);
            return FromType(details.Type, tiers);
        }

        private static List<string> FromType(string type, List<string> tiers)
        {
            var result = new List<string>();
            foreach (string tier in tiers)
            {
                result = AtTier(type, tier);
                if (result.Count > 0) return result;
                if (Rules.FallbackByType.TryGetValue(type, out var fallback) &&
                    !string.IsNullOrEmpty(fallback.Type) && fallback.Type != type)
                {
                    result = AtTier(fallback.Type, tier);
                    if (result.Count > 0) return result;
                }
            }
            if (Rules.FallbackByType.TryGetValue(type, out var last) && IsCraftingEligibleAtTiers(last.Item, tiers))
                result.Add(last.Item);
            return result;
        }

        internal static bool IsCraftingEligible(string item) => !string.IsNullOrEmpty(item) && !LootDenyList.IsDenied(item) &&
            (TreasureProgression.Current != null ? TreasureProgression.Current.Unlocked.Contains(item) : CraftingHistory.IsUnlocked(item));

        internal static bool IsCraftingEligibleAtTiers(string item, List<string> tiers) => IsCraftingEligible(item) &&
            Rules.AllItemsWithDetails.TryGetValue(item, out var details) && details.RequiredBosses.Any(tiers.Contains);

        private static List<string> AtTier(string type, string tier)
        {
            if (!Rules.ItemsByTypeAndBoss.TryGetValue(type, out var table) || !table.TryGetValue(tier, out var items))
                return new List<string>();
            return items.Where(IsCraftingEligible).Distinct().ToList();
        }
    }

    [HarmonyPatch(typeof(Rules), nameof(Rules.GetGatedItemNameFromItemOrType))]
    internal static class EquipmentRoll
    {
        // The item or type of the last roll, when it had nothing eligible.
        internal static string Withheld;

        private static bool Prefix(string itemOrType, GatedItemTypeMode gatedMode, ref string __result)
        {
            Withheld = null;
            if (gatedMode != GatedItemTypeMode.PlayerMustHaveCraftedItem) return true;
            if (Player.m_localPlayer == null && TreasureProgression.Current == null)
            {
                if (!BossLoot.NeedsFallback && !BountyLoot.NeedsFallback) return true;
                __result = "Club";
                return false;
            }
            var candidates = Eligibility.Candidates(itemOrType);
            // Guaranteed boss and bounty rewards fall back to a basic club when no equipment is craft-eligible.
            // The creature's original loot branch still sets rarity; this does not unlock crafting recipes.
            if (candidates.Count == 0 && (BossLoot.NeedsFallback || BountyLoot.NeedsFallback))
            {
                __result = "Club";
                return false;
            }
            if (candidates.Count == 0) Withheld = itemOrType;
            __result = candidates.Count == 0 ? null : candidates[UnityEngine.Random.Range(0, candidates.Count)];
            return false;
        }
    }

    // EpicLoot 0.14.13 logs a roll that crafting history withholds as a missing item with an empty name: "Tried to spawn
    // loot () for (Deathsquito), but the item prefab was not found!" The rolling player's log names the withheld item
    // instead, as information. Any other missing item keeps EpicLoot's error.
    internal static class WithheldEquipmentLog
    {
        private static ManualLogSource logger;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            logger = log;
            harmony.Patch(AccessTools.Method(typeof(LootRoller), "SpawnNormalItem"),
                transpiler: new HarmonyMethod(typeof(WithheldEquipmentLog), nameof(Transpiler)));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var logError = AccessTools.Method(typeof(EpicLoot.EpicLoot), nameof(EpicLoot.EpicLoot.LogError));
            int index = code.FindIndex(x => x.Calls(logError));
            // EpicLoot.LogError("Tried to spawn loot (" + itemName + ") for (" + objectName + "), but the item prefab was not found!");
            if (index < 0 || code.FindLastIndex(x => x.Calls(logError)) != index || code[index].labels.Count > 0 ||
                !code.Take(index).Any(x => x.opcode == OpCodes.Ldstr && x.operand as string == "Tried to spawn loot ("))
                throw new InvalidOperationException("EpicLoot's item spawn changed; expected one missing-item error.");
            // Also pass the object whose loot it is.
            code.Insert(index, new CodeInstruction(OpCodes.Ldarg_1));
            code[index + 1].operand = AccessTools.Method(typeof(WithheldEquipmentLog), nameof(Report));
            return code;
        }

        private static void Report(string error, string source)
        {
            string rolled = EquipmentRoll.Withheld;
            EquipmentRoll.Withheld = null;
            string type = rolled == null || LootDenyList.IsDenied(rolled) ? null :
                Rules.AllItemsWithDetails.TryGetValue(rolled, out var details) ? details.Type :
                Rules.ItemsByTypeAndBoss.ContainsKey(rolled) ? rolled : null;
            if (type == null) EpicLoot.EpicLoot.LogError(error);
            else logger.LogInfo($"Withheld {rolled} from {source?.Replace("(Clone)", "")}'s loot: " +
                (TreasureProgression.Current != null ? $"treasure buyer {TreasureProgression.Current.BuyerId}'s" : $"{Player.m_localPlayer.GetPlayerName()}'s") + $" crafting history has no {type} or their fallback up to that tier.");
        }
    }

    [HarmonyPatch(typeof(Rules), nameof(Rules.GetGatedItemFromType), new[] { typeof(string), typeof(GatedItemTypeMode),
        typeof(HashSet<string>), typeof(List<string>), typeof(bool), typeof(bool), typeof(bool), typeof(Random) })]
    internal static class EquipmentFallback
    {
        private static void Postfix(GatedItemTypeMode mode, List<string> validBosses, ref string __result)
        {
            if (mode == GatedItemTypeMode.PlayerMustHaveCraftedItem && Player.m_localPlayer != null &&
                !Eligibility.IsCraftingEligibleAtTiers(__result, validBosses)) __result = null;
        }
    }
}
