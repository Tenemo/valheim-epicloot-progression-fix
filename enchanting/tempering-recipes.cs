using System.Collections.Generic;
using System.Linq;
using EpicLoot;
using EpicLoot.Adventure;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(TemperMan), nameof(TemperMan.ApplyConfig))]
    internal static class InvalidTemperRecipes
    {
        internal static void Initialize() => Postfix(AdventureDataManager.Config?.Tempering);

        private static void Postfix(TemperingConfig config)
        {
            if (config?.CostsByRarity == null) return;
            foreach (var entry in config.CostsByRarity)
            {
                if (entry.Value == null || entry.Value.Any(cost => cost == null || string.IsNullOrWhiteSpace(cost.Item) || cost.Amount <= 0))
                {
                    TemperMan.costMap.Remove(entry.Key);
                    EpicLoot.EpicLoot.LogError($"Tempering recipe for {entry.Key} is invalid; correct its material costs.");
                }
            }
        }
    }

    [HarmonyPatch(typeof(TemperMan), nameof(TemperMan.IsTemperableRarity))]
    internal static class MissingTemperMaterials
    {
        private static readonly HashSet<string> Reported = new HashSet<string>();

        private static void Postfix(ItemRarity rarity, ref bool __result)
        {
            if (!__result) return;
            foreach (var requirement in TemperMan.GetRequirements(rarity))
            {
                if (requirement.isValid) continue;
                __result = false;
                if (Reported.Add(rarity + ":" + requirement.prefab))
                    EpicLoot.EpicLoot.LogError($"Tempering requires missing material {requirement.prefab}; {rarity} recipe is unavailable.");
            }
        }
    }
}
