using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using BepInEx.Configuration;
using EpicLoot;
using EpicLoot.LegendarySystem;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal sealed class EnchantProbabilityInput
    {
        internal EnchantProbabilities.Input Numbers;
        internal MagicItemEffectDefinition[] Effects;
        internal MagicItemEffectDefinition.ValueDef[] Ranges;
        internal string Key;
        private static readonly ConfigEntry<float> SetChance = (ConfigEntry<float>)AccessTools.Field(
            AccessTools.TypeByName("EpicLoot.Config.ELConfig"), "SetItemDropChance").GetValue(null);

        // Random.Range(0f, 1f) uses the same native ticket word as Random.value, with reversed endpoints.
        internal static float RangeTicket(int ticket) => (float)(1f - AugmentProbabilities.TicketValue(ticket));
        internal static int SetTickets(float chance)
        {
            int low = 0, high = AugmentProbabilities.TicketCount;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (RangeTicket(middle) < chance) high = middle; else low = middle + 1;
            }
            return AugmentProbabilities.TicketCount - low;
        }

        internal static EnchantProbabilityInput Capture(ItemDrop.ItemData item, ItemRarity rarity)
        {
            var definitions = MagicItemEffectDefinitions.AllDefinitions.Values.ToArray();
            if (definitions.Any(d => d.Requirements == null || !d.Requirements.ExclusiveSelf || d.Requirements.ExternalRequirements?.Count > 0))
                throw new NotSupportedException("Enchant requirements include repeatable effects or external rules that have not been verified.");
            var triggers = definitions.SelectMany(d => d.Requirements.MustHaveEffectTypes).Concat(new[] { MagicEffectType.Throwable, MagicEffectType.Bloodlust })
                .Distinct().Where(t => MagicItemEffectDefinitions.AllDefinitions.ContainsKey(t)).ToArray();
            if (triggers.Length > 8) throw new NotSupportedException("Enchant prerequisites exceed the verified snapshot size.");
            var counts = LootRoller.GetEffectCountsPerRarity(rarity, true);
            var countTickets = EnchantProbabilities.Tickets(counts.Select(c => c.Value).ToArray());
            var empty = new MagicItem { Rarity = rarity };
            var templates = new List<(LegendaryInfo Template, EnchantProbabilities.Fraction Weight)>();
            int setTickets = 0;
            if (UniqueLegendaryHelper.AnySetEnabledAt(rarity))
            {
                var sets = UniqueLegendaryHelper.GetAvailableSetPieces(item, empty, rarity);
                if (sets.Count > 0)
                {
                    setTickets = SetTickets(SetChance.Value);
                    AddTemplates(sets, new EnchantProbabilities.Fraction(setTickets, 23));
                }
            }
            var regularWeight = new EnchantProbabilities.Fraction(AugmentProbabilities.TicketCount - setTickets, 23);
            if (UniqueLegendaryHelper.AnyUniqueEnabledAt(rarity)) AddTemplates(UniqueLegendaryHelper.GetAvailableUniques(item, empty, rarity), regularWeight);
            else templates.Add((null, regularWeight));
            void AddTemplates(List<LegendaryInfo> source, EnchantProbabilities.Fraction gate)
            {
                var tickets = EnchantProbabilities.Tickets(source.Select(t => t.SelectionWeight).ToArray());
                for (int i = 0; i < source.Count; i++)
                    if (tickets[i] > 0) templates.Add((source[i], gate * new EnchantProbabilities.Fraction(tickets[i], 23)));
                if (tickets[source.Count] > 0) templates.Add((null, gate * new EnchantProbabilities.Fraction(tickets[source.Count], 23)));
            }

            var result = new EnchantProbabilityInput();
            var output = new List<MagicItemEffectDefinition>();
            var ranges = new Dictionary<string, MagicItemEffectDefinition.ValueDef>();
            void IncludeRange(string type, MagicItemEffectDefinition.ValueDef value)
            {
                if (value == null) return;
                if (!ranges.TryGetValue(type, out var range)) ranges[type] = new MagicItemEffectDefinition.ValueDef
                    { MinValue = value.MinValue, MaxValue = value.MaxValue, Increment = value.Increment };
                else { range.MinValue = Math.Min(range.MinValue, value.MinValue); range.MaxValue = Math.Max(range.MaxValue, value.MaxValue); }
            }
            int Output(string type)
            {
                int index = output.FindIndex(d => d.Type == type);
                if (index < 0) { index = output.Count; output.Add(MagicItemEffectDefinitions.Get(type)); }
                return index;
            }
            var branches = new List<EnchantProbabilities.Branch>();
            foreach (var selection in templates.Where(t => !t.Weight.Numerator.IsZero))
            {
                var template = selection.Template;
                bool named = template != null && !UniqueLegendaryHelper.IsGenericLegendary(template);
                var magic = new MagicItem { Rarity = rarity, SetID = template == null ? null : UniqueLegendaryHelper.GetSetForLegendaryItem(template),
                    LegendaryID = named ? template.ID : null };
                if (named)
                    foreach (var guaranteed in template.GuaranteedMagicEffects)
                        if (MagicItemEffectDefinitions.TryGet(guaranteed.Type, out var definition))
                        {
                            magic.Effects.Add(new MagicItemEffect(definition.Type, 1));
                            IncludeRange(definition.Type, UniqueLegendaryHelper.ResolveValues(guaranteed, rarity) ?? definition.GetValuesForRarity(rarity));
                        }
                var guarantees = magic.Effects.Select(e => e.EffectType).ToArray();
                var pools = new List<MagicItemEffectDefinition>[1 << triggers.Length];
                for (int context = 0; context < pools.Length; context++)
                {
                    magic.Effects = guarantees.Select(t => new MagicItemEffect(t, 1)).ToList();
                    for (int t = 0; t < triggers.Length; t++)
                        if ((context & (1 << t)) != 0 && !guarantees.Contains(triggers[t])) magic.Effects.Add(new MagicItemEffect(triggers[t], 1));
                    pools[context] = MagicItemEffectDefinitions.GetAvailableEffects(item, magic);
                }
                var universe = new HashSet<string>(pools.SelectMany(p => p).Select(d => d.Type));
                var effects = definitions.Where(d => universe.Contains(d.Type)).ToArray();
                foreach (var effect in effects) IncludeRange(effect.Type, effect.GetValuesForRarity(rarity));
                if (effects.Length > 64) throw new NotSupportedException("Enchant has more than 64 possible random effects for this item.");
                ulong Mask(IEnumerable<string> types)
                {
                    var set = new HashSet<string>(types); ulong mask = 0;
                    for (int i = 0; i < effects.Length; i++) if (set.Contains(effects[i].Type)) mask |= 1UL << i;
                    return mask;
                }
                var exclusions = effects.Select(d => Mask(effects.Where(other => other.Type == d.Type ||
                    MagicItemEffectRequirements.EffectsExclude(d.Type, other.Type)).Select(other => other.Type))).ToArray();
                int remaining(int count) => Math.Max(0, (named && template.GuaranteedEffectCount > 0 ? template.GuaranteedEffectCount : count) - guarantees.Length);
                int maximum = counts.Count == 0 ? remaining(0) : counts.Max(c => remaining(c.Key));
                var distribution = new EnchantProbabilities.Fraction[maximum + 1];
                for (int i = 0; i < counts.Count; i++) distribution[remaining(counts[i].Key)] += new EnchantProbabilities.Fraction(countTickets[i], 23);
                // An empty count table or its native default ticket gives count zero.
                distribution[remaining(0)] += new EnchantProbabilities.Fraction(countTickets[counts.Count], 23);
                branches.Add(new EnchantProbabilities.Branch { Weights = effects.Select(d => d.SelectionWeight).ToArray(),
                    Enabled = pools.Select(p => Mask(p.Select(d => d.Type))).ToArray(), Exclusions = exclusions,
                    TriggerBits = effects.Select(d => { int index = Array.IndexOf(triggers, d.Type); return index < 0 ? 0 : 1 << index; }).ToArray(),
                    OutputIndices = effects.Select(d => Output(d.Type)).ToArray(), Guaranteed = guarantees.Select(Output).ToArray(), Counts = distribution, Weight = selection.Weight });
            }
            var canonical = definitions.Where(d => output.Contains(d)).ToArray();
            var remap = output.Select(d => Array.IndexOf(canonical, d)).ToArray();
            foreach (var branch in branches)
            {
                branch.OutputIndices = branch.OutputIndices.Select(i => remap[i]).ToArray();
                branch.Guaranteed = branch.Guaranteed.Select(i => remap[i]).ToArray();
            }
            output = canonical.ToList();
            result.Effects = canonical;
            result.Ranges = output.Select(d => ranges.TryGetValue(d.Type, out var range) ? range : null).ToArray();
            result.Numbers = new EnchantProbabilities.Input { EffectCount = output.Count, Branches = branches.ToArray() };
            var key = new StringBuilder();
            foreach (var effect in output) key.Append(effect.Type).Append('|');
            foreach (var branch in branches)
            {
                key.Append(';').Append(branch.Weight.Numerator).Append('/').Append(branch.Weight.Bits);
                foreach (var count in branch.Counts) key.Append(':').Append(count.Numerator).Append('/').Append(count.Bits);
                foreach (var weight in branch.Weights) key.Append(',').Append(BitConverter.ToInt32(BitConverter.GetBytes(weight), 0));
                key.Append('|').Append(string.Join(",", branch.Enabled)).Append('|').Append(string.Join(",", branch.Exclusions))
                    .Append('|').Append(string.Join(",", branch.TriggerBits)).Append('|').Append(string.Join(",", branch.OutputIndices)).Append('|').Append(string.Join(",", branch.Guaranteed));
            }
            result.Key = key.ToString();
            return result;
        }
    }
}
