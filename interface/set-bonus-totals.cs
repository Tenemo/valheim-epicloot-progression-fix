using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpicLoot;
using EpicLoot.LegendarySystem;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // A set row whose effect an earlier row of the set already gives adds to it, as Coiling Venom's four-piece half does.
    // The item tooltip, the set rune and the Compendium show such a row's line and explanation with the value the
    // player has from that many pieces: its own value added to the earlier rows'.
    internal static class SetBonusTotals
    {
        private static readonly ItemRarity[] Rarities = { ItemRarity.Magic, ItemRarity.Rare, ItemRarity.Epic, ItemRarity.Legendary, ItemRarity.Mythic, ItemRarity.Ancient };

        // The row with its value at each rarity added to the earlier rows of the same effect, or the row itself when
        // none come before it or it is not a set's row.
        private static SetBonusInfo Stacked(SetBonusInfo bonus)
        {
            if (bonus?.Effect == null) return bonus;
            foreach (var set in UniqueLegendaryHelper.AllSets.Values)
            {
                if (set?.SetBonuses == null || !set.SetBonuses.Contains(bonus)) continue;
                // The rows in the order EpicLoot lists them.
                var rows = set.SetBonuses.Where(row => row?.Effect != null).OrderBy(row => row.Count).ToList();
                var stack = rows.Take(rows.IndexOf(bonus) + 1).Where(row => row.Effect.Type == bonus.Effect.Type).ToList();
                return stack.Count == 1 ? bonus : Sum(bonus, stack);
            }
            return bonus;
        }

        private static SetBonusInfo Sum(SetBonusInfo bonus, List<SetBonusInfo> stack)
        {
            var values = new MagicItemEffectDefinition.ValuesPerRarityDef();
            foreach (var rarity in Rarities)
            {
                float total = stack.Sum(row => SetBonusEvaluator.GetBonusValue(row, rarity));
                var value = new MagicItemEffectDefinition.ValueDef { MinValue = total, MaxValue = total, Increment = 1f };
                switch (rarity)
                {
                    case ItemRarity.Magic: values.Magic = value; break;
                    case ItemRarity.Rare: values.Rare = value; break;
                    case ItemRarity.Epic: values.Epic = value; break;
                    case ItemRarity.Legendary: values.Legendary = value; break;
                    case ItemRarity.Mythic: values.Mythic = value; break;
                    case ItemRarity.Ancient: values.Ancient = value; break;
                }
            }
            return new SetBonusInfo { Count = bonus.Count, Effect = new GuaranteedMagicEffect { Type = bonus.Effect.Type, Values = values.Legendary, ValuesPerRarity = values } };
        }

        // The set rune and the Compendium.
        [HarmonyPatch]
        private static class RuneRows
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SetDescriptions), nameof(SetDescriptions.DescribeSetBonus));
                yield return AccessTools.Method(typeof(SetDescriptions), nameof(SetDescriptions.DescribeSetBonusDetail),
                    new[] { typeof(MagicItemEffectDefinition), typeof(SetBonusInfo), typeof(List<ItemRarity>) });
            }

            private static void Prefix(ref SetBonusInfo bonus) => bonus = Stacked(bonus);
        }

        // A worn or held piece's tooltip, whose rows hold the value at the tier the player has or would have.
        [HarmonyPatch]
        private static class PieceRows
        {
            private static MethodBase TargetMethod() => AccessTools.Method(MagicTooltipContext.Gatherer, "GatherLegendarySet");

            private static void Postfix(object __instance)
            {
                var sets = ((ItemTooltipData)MagicTooltipContext.Data.GetValue(__instance)).Sets;
                if (sets.Count == 0) return;
                var totals = new Dictionary<string, float>();
                foreach (var row in sets[sets.Count - 1].Bonuses)
                {
                    if (row == null || !MagicItemEffectDefinitions.TryGet(row.EffectType, out var definition)) continue;
                    bool repeated = totals.TryGetValue(row.EffectType, out float earlier);
                    float total = totals[row.EffectType] = earlier + row.Value;
                    if (!repeated) continue;
                    row.Text = MagicItem.GetEffectText(definition, total);
                    if (row.Detail != null) row.Detail = SetDescriptions.DescribeSetBonusDetail(definition, total);
                }
            }
        }
    }
}
