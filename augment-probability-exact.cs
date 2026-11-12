using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using System.Threading;

namespace EpicLootProgressionFix
{
    internal static class AugmentProbabilityExact
    {
        internal static (BigInteger[] Numerators, int DenominatorBits) Calculate(float[] weights, bool[] removable, int choices, CancellationToken cancellation = default)
        {
            const int ticketBits = 23, offerBits = 6 * ticketBits;
            if (!removable.Any(value => value))
                return FixedPool(weights, choices, cancellation);
            int count = weights.Length, bits = 0;
            choices = Math.Min(choices, count);
            var hits = new BigInteger[count];
            var itemBits = new BigInteger[count];
            BigInteger removableMask = 0;
            for (int i = 0; i < count; i++)
            {
                itemBits[i] = BigInteger.One << i;
                if (removable[i])
                    removableMask |= itemBits[i];
            }

            var transitions = new Dictionary<BigInteger, int[]>();
            int[] Tickets(BigInteger removed)
            {
                if (transitions.TryGetValue(removed, out var existing))
                    return existing;
                var result = CountTickets(weights, removed);
                return transitions[removed] = result;
            }

            var states = new Dictionary<BigInteger, BigInteger>
            {
                [BigInteger.Zero] = BigInteger.One
            };
            for (int offer = 0; offer < choices && states.Count > 0; offer++)
            {
                cancellation.ThrowIfCancellationRequested();
                bits += offerBits;
                for (int i = 0; i < count; i++)
                    hits[i] <<= offerBits;
                var next = new Dictionary<BigInteger, BigInteger>();
                void Add(BigInteger state, BigInteger mass)
                {
                    if (mass.IsZero)
                        return;
                    next.TryGetValue(state, out var prior);
                    next[state] = prior + mass;
                }

                int visited = 0;
                foreach (var state in states)
                {
                    if ((visited++ & 127) == 0)
                        cancellation.ThrowIfCancellationRequested();
                    var removed = state.Key & removableMask;
                    int removedCount = 0;
                    for (var value = removed; !value.IsZero; value &= value - 1)
                        removedCount++;
                    if (offer >= count - removedCount)
                        continue;
                    var probabilities = Tickets(removed);
                    int duplicate = 0;
                    for (int i = 0; i < count; i++)
                        if (!removable[i] && (state.Key & itemBits[i]) != 0)
                            duplicate += probabilities[i];
                    BigInteger retry = duplicate + probabilities[count], series = 1;
                    for (int power = 1; power <= 4; power++)
                        series = series * retry + (BigInteger.One << (ticketBits * power));
                    var unseen = state.Value * ((BigInteger.One << (ticketBits * 5)) + duplicate * series);
                    for (int i = 0; i < count; i++)
                    {
                        if (probabilities[i] == 0 || (state.Key & itemBits[i]) != 0)
                            continue;
                        var mass = unseen * probabilities[i];
                        hits[i] += mass;
                        Add(state.Key | itemBits[i], mass);
                    }

                    Add(state.Key, state.Value * duplicate * BigInteger.Pow(retry, 5));
                }

                states = next;
            }

            return (hits, bits);
        }

        private static int[] CountTickets(float[] weights, BigInteger removed)
        {
            var result = new int[weights.Length + 1];
            double sum = 0;
            for (int i = 0; i < weights.Length; i++)
                if ((removed & (BigInteger.One << i)) == 0)
                    sum += weights[i];
            float total = (float)sum, cumulative = 0;
            int previous = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                if ((removed & (BigInteger.One << i)) != 0)
                    continue;
                cumulative = (float)(cumulative + weights[i]);
                int through = AugmentProbabilities.TicketsAtOrBelow(cumulative, total);
                result[i] = through - previous;
                previous = through;
            }

            result[weights.Length] = AugmentProbabilities.TicketCount - previous;
            return result;
        }

        // A pool without removals has a fixed sampler. Group identities only when their native integer ticket counts
        // are identical. Their expected number of first appearances, divided by group size, is each member's probability.
        private static (BigInteger[] Numerators, int DenominatorBits) FixedPool(float[] weights, int choices, CancellationToken cancellation)
        {
            const int ticketBits = 23, offerBits = 6 * ticketBits;
            choices = Math.Min(choices, weights.Length);
            var tickets = CountTickets(weights, BigInteger.Zero);
            var membership = new int[weights.Length];
            var sizes = new List<int>();
            var probability = new List<int>();
            var groups = new Dictionary<int, int>();
            for (int i = 0; i < weights.Length; i++)
            {
                if (!groups.TryGetValue(tickets[i], out int group))
                {
                    group = sizes.Count;
                    groups.Add(tickets[i], group);
                    sizes.Add(0);
                    probability.Add(tickets[i]);
                }

                membership[i] = group;
                sizes[group]++;
            }

            int width = 1;
            while ((BigInteger.One << width) <= choices)
                width++;
            var digitMask = (BigInteger.One << width) - 1;
            var units = Enumerable.Range(0, sizes.Count).Select(group => BigInteger.One << (width * group)).ToArray();
            var hits = new BigInteger[sizes.Count];
            var seen = new int[sizes.Count];
            int bits = 0;
            var states = new Dictionary<BigInteger, BigInteger>
            {
                {
                    BigInteger.Zero,
                    BigInteger.One
                }
            };
            for (int offer = 0; offer < choices; offer++)
            {
                cancellation.ThrowIfCancellationRequested();
                bits += offerBits;
                for (int group = 0; group < hits.Length; group++)
                    hits[group] <<= offerBits;
                var next = new Dictionary<BigInteger, BigInteger>();
                void Add(BigInteger key, BigInteger mass)
                {
                    if (mass.IsZero)
                        return;
                    next.TryGetValue(key, out var prior);
                    next[key] = prior + mass;
                }

                int visited = 0;
                foreach (var state in states)
                {
                    if ((visited++ & 127) == 0)
                        cancellation.ThrowIfCancellationRequested();
                    int duplicate = 0;
                    for (int group = 0; group < sizes.Count; group++)
                    {
                        seen[group] = (int)((state.Key >> (width * group)) & digitMask);
                        duplicate += seen[group] * probability[group];
                    }

                    BigInteger retry = duplicate + tickets[weights.Length], series = 1;
                    for (int power = 1; power <= 4; power++)
                        series = series * retry + (BigInteger.One << (ticketBits * power));
                    var unseen = state.Value * ((BigInteger.One << (ticketBits * 5)) + duplicate * series);
                    for (int group = 0; group < sizes.Count; group++)
                    {
                        var mass = unseen * (sizes[group] - seen[group]) * probability[group];
                        hits[group] += mass;
                        Add(state.Key + units[group], mass);
                    }

                    Add(state.Key, state.Value * duplicate * BigInteger.Pow(retry, 5));
                }

                states = next;
            }

            return (membership.Select(group => hits[group] / sizes[group]).ToArray(), bits);
        }

        internal static int Round(BigInteger numerator, int denominatorBits) => denominatorBits == 0 ? (int)(numerator * 10000) : (int)((numerator * 10000 + (BigInteger.One << (denominatorBits - 1))) >> denominatorBits);
    }
}
