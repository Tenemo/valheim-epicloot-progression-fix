using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

namespace EpicLootProgressionFix
{
    internal static class AugmentProbabilityIntervals
    {
        private sealed class Accumulator
        {
            internal readonly double[] Hits, Aggregate, Excluded, NegativeAggregate, NegativeExcluded, Coefficients, Polynomial = new double[6];
            internal readonly int[] Selected, Prefix, Suffix;
            internal long VisitedStates;
            internal Accumulator(int effectCount, int removalStateCount, int maximumSeen)
            {
                Hits = new double[effectCount];
                Aggregate = new double[removalStateCount];
                Excluded = new double[removalStateCount * effectCount];
                NegativeAggregate = new double[removalStateCount];
                NegativeExcluded = new double[removalStateCount * effectCount];
                Coefficients = new double[removalStateCount * 6];
                Selected = new int[maximumSeen];
                Prefix = new int[maximumSeen + 1];
                Suffix = new int[maximumSeen + 1];
            }

            internal void Add(Accumulator other)
            {
                void AddArray(double[] target, double[] source)
                {
                    for (int i = 0; i < target.Length; i++)
                        target[i] += source[i];
                }

                AddArray(Hits, other.Hits);
                AddArray(Aggregate, other.Aggregate);
                AddArray(Excluded, other.Excluded);
                AddArray(NegativeAggregate, other.NegativeAggregate);
                AddArray(NegativeExcluded, other.NegativeExcluded);
                AddArray(Coefficients, other.Coefficients);
                VisitedStates += other.VisitedStates;
            }
        }

        const int Tickets = AugmentProbabilities.TicketCount;
        internal readonly struct Bounds
        {
            internal readonly double Low, High;
            internal Bounds(double low, double high)
            {
                Low = low;
                High = high;
            }
        }

        static double Down(double value) => value == 0 ? -double.Epsilon : BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) + (value > 0 ? -1 : 1));
        static double Up(double value) => value == 0 ? double.Epsilon : BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) + (value > 0 ? 1 : -1));
        internal static Bounds[] Calculate(float[] weights, bool[] removed, int choices, CancellationToken cancellation = default)
        {
            if (weights.Length == 0 || choices == 0)
                return new Bounds[weights.Length];
            choices = Math.Min(choices, weights.Length);
            int effectCount = weights.Length, maximumSeen = Math.Min(choices - 2, effectCount);
            var removalBits = new int[effectCount];
            int removableCount = 0;
            for (int i = 0; i < effectCount; i++)
                if (removed[i])
                    removalBits[i] = 1 << removableCount++;
            int removalStateCount = 1 << removableCount;
            var selectionProbabilities = new double[removalStateCount][];
            var nullProbabilities = new double[removalStateCount];
            var remainingEffects = new int[removalStateCount];
            for (int removedMask = 0; removedMask < removalStateCount; removedMask++)
            {
                if ((removedMask & 15) == 0)
                    cancellation.ThrowIfCancellationRequested();
                double sum = 0;
                for (int i = 0; i < effectCount; i++)
                    if ((removedMask & removalBits[i]) == 0)
                    {
                        sum += weights[i];
                        remainingEffects[removedMask]++;
                    }

                float total = (float)sum, cumulative = 0;
                int previous = 0;
                var currentSelection = selectionProbabilities[removedMask] = new double[effectCount];
                if (remainingEffects[removedMask] == 0)
                {
                    nullProbabilities[removedMask] = 1;
                    continue;
                }

                for (int i = 0; i < effectCount; i++)
                {
                    if ((removedMask & removalBits[i]) != 0)
                        continue;
                    cumulative = (float)(cumulative + weights[i]);
                    int boundary = AugmentProbabilities.TicketsAtOrBelow(cumulative, total);
                    currentSelection[i] = (double)(boundary - previous) / Tickets;
                    previous = boundary;
                }

                nullProbabilities[removedMask] = (double)(Tickets - previous) / Tickets;
            }

            if (choices == 1)
                return Array.ConvertAll(selectionProbabilities[0], value => new Bounds(value, value));
            var choose = new int[maximumSeen + 1][];
            for (int seenCount = 0; seenCount <= maximumSeen; seenCount++)
                choose[seenCount] = new int[effectCount + 1];
            for (int i = 0; i <= effectCount; i++)
            {
                choose[0][i] = 1;
                for (int seenCount = 1; seenCount <= Math.Min(i, maximumSeen); seenCount++)
                    choose[seenCount][i] = checked(choose[seenCount - 1][i - 1] + choose[seenCount][i - 1]);
            }

            var offsets = new int[maximumSeen + 1];
            int size = 0;
            for (int seenCount = 0; seenCount <= maximumSeen; seenCount++)
            {
                offsets[seenCount] = size;
                size += choose[seenCount][effectCount];
            }

            var unseenMass = new double[size];
            var duplicateMass = new double[size];
            unseenMass[0] = 1;
            var nextUnseenMass = new double[size];
            var nextDuplicateMass = new double[size];
            var totals = new Accumulator(effectCount, removalStateCount, maximumSeen);
            var moments = new double[removalStateCount * 6];
            for (int removedMask = 0; removedMask < removalStateCount; removedMask++)
                for (int i = 0; i < effectCount; i++)
                    if (removalBits[i] == 0)
                    {
                        double probability = selectionProbabilities[removedMask][i], power = probability;
                        for (int degree = 0; degree < 6; degree++)
                        {
                            moments[removedMask * 6 + degree] += power;
                            power *= probability;
                        }
                    }

            var removableItems = new List<int>();
            for (int i = 0; i < effectCount; i++)
                if (removalBits[i] != 0)
                    removableItems.Add(i);
            void Finish(int seenCount, int removedMask, double weightedMass, double duplicateProbability, Accumulator result)
            {
                var selected = result.Selected;
                var hits = result.Hits;
                var aggregate = result.Aggregate;
                var excluded = result.Excluded;
                var negativeAggregate = result.NegativeAggregate;
                var negativeExcluded = result.NegativeExcluded;
                var coefficients = result.Coefficients;
                var polynomial = result.Polynomial;
                if (weightedMass == 0)
                    return;
                double positiveMass = weightedMass, negative = 0;
                if (choices - 1 < remainingEffects[removedMask])
                {
                    double retryProbability = duplicateProbability + nullProbabilities[removedMask], retrySquared = retryProbability * retryProbability, retryCubed = retrySquared * retryProbability, retryFourth = retrySquared * retrySquared;
                    double retrySum = 1 + retryProbability + retrySquared + retryCubed + retryFourth, firstCoefficient = 1 + 2 * retryProbability + 3 * retrySquared + 4 * retryCubed, secondCoefficient = 1 + 3 * retryProbability + 6 * retrySquared, thirdCoefficient = 1 + 4 * retryProbability;
                    polynomial[0] = 1 + duplicateProbability * retrySum;
                    polynomial[1] = retrySum + duplicateProbability * firstCoefficient;
                    polynomial[2] = firstCoefficient + duplicateProbability * secondCoefficient;
                    polynomial[3] = secondCoefficient + duplicateProbability * thirdCoefficient;
                    polynomial[4] = thirdCoefficient + duplicateProbability;
                    polynomial[5] = 1;
                    double sum = 0;
                    for (int degree = 0; degree < 6; degree++)
                    {
                        sum += polynomial[degree] * moments[removedMask * 6 + degree];
                        coefficients[removedMask * 6 + degree] += weightedMass * polynomial[degree];
                    }

                    for (int j = 0; j < seenCount; j++)
                        if (removalBits[selected[j]] == 0)
                        {
                            double probability = selectionProbabilities[removedMask][selected[j]], retry = retryProbability + probability;
                            double value = probability * (1 + (duplicateProbability + probability) * (1 + retry * (1 + retry * (1 + retry * (1 + retry)))));
                            negative += weightedMass * value;
                            hits[selected[j]] += weightedMass * probability * value;
                        }

                    positiveMass += weightedMass * (duplicateProbability * retryFourth * retryProbability + sum);
                }

                aggregate[removedMask] += positiveMass;
                negativeAggregate[removedMask] += negative;
                for (int j = 0; j < seenCount; j++)
                {
                    excluded[removedMask * effectCount + selected[j]] += positiveMass;
                    negativeExcluded[removedMask * effectCount + selected[j]] += negative;
                }

                foreach (int item in removableItems)
                {
                    if (removalBits[item] == 0 || (removedMask & removalBits[item]) != 0)
                        continue;
                    int next = removedMask | removalBits[item];
                    if (choices - 1 >= remainingEffects[next])
                        continue;
                    double nextDuplicateProbability = 0;
                    var nextSelection = selectionProbabilities[next];
                    for (int j = 0; j < seenCount; j++)
                        if (removalBits[selected[j]] == 0)
                            nextDuplicateProbability += nextSelection[selected[j]];
                    double retryProbability = nextDuplicateProbability + nullProbabilities[next];
                    double bonus = weightedMass * selectionProbabilities[removedMask][item] * (1 + nextDuplicateProbability * (1 + retryProbability * (1 + retryProbability * (1 + retryProbability * (1 + retryProbability)))));
                    aggregate[next] += bonus;
                    excluded[next * effectCount + item] += bonus;
                    for (int j = 0; j < seenCount; j++)
                        excluded[next * effectCount + selected[j]] += bonus;
                }
            }

            void Visit(int offersMade, int seenCount, int subsetIndex, Accumulator result)
            {
                if ((subsetIndex & 255) == 0)
                    cancellation.ThrowIfCancellationRequested();
                var selected = result.Selected;
                var prefix = result.Prefix;
                var suffix = result.Suffix;
                var hits = result.Hits;
                result.VisitedStates++;
                int removedMask = 0;
                for (int i = 0; i < seenCount; i++)
                    removedMask |= removalBits[selected[i]];
                int address = offsets[seenCount] + subsetIndex;
                double mass = duplicateMass[address];
                prefix[0] = 0;
                suffix[seenCount] = 0;
                for (int i = 0; i < seenCount; i++)
                    prefix[i + 1] = prefix[i] + choose[i + 1][selected[i]];
                for (int i = seenCount - 1; i >= 0; i--)
                    suffix[i] = suffix[i + 1] + (i == 0 ? 0 : choose[i][selected[i]]);
                for (int i = 0; i < seenCount; i++)
                {
                    int item = selected[i];
                    int predecessor = offsets[seenCount - 1] + prefix[i] + suffix[i + 1];
                    double incoming = unseenMass[predecessor] * selectionProbabilities[removedMask ^ removalBits[item]][item];
                    mass += incoming;
                    hits[item] += incoming;
                }

                double weightedMass = 0, duplicateMassNext = 0, duplicateProbability = 0;
                if (offersMade < remainingEffects[removedMask])
                {
                    var currentSelection = selectionProbabilities[removedMask];
                    for (int i = 0; i < seenCount; i++)
                        if (removalBits[selected[i]] == 0)
                            duplicateProbability += currentSelection[selected[i]];
                    double retry = duplicateProbability + nullProbabilities[removedMask];
                    double series = 1 + retry * (1 + retry * (1 + retry * (1 + retry)));
                    weightedMass = mass * (1 + duplicateProbability * series);
                    duplicateMassNext = mass * duplicateProbability * retry * retry * retry * retry * retry;
                }

                nextUnseenMass[address] = weightedMass;
                nextDuplicateMass[address] = duplicateMassNext;
                if (offersMade == choices - 2)
                    Finish(seenCount, removedMask, weightedMass, duplicateProbability, result);
            }

            for (int offersMade = 1; offersMade <= choices - 2; offersMade++)
            {
                for (int seenCount = 1; seenCount <= Math.Min(offersMade, maximumSeen); seenCount++)
                {
                    if (seenCount == offersMade && offersMade == choices - 2 && choose[seenCount][effectCount] >= 4096)
                    {
                        object gate = new object ();
                        Parallel.For(seenCount - 1, effectCount, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellation }, () => new Accumulator(effectCount, removalStateCount, maximumSeen), (last, loop, local) =>
                        {
                            var selected = local.Selected;
                            for (int i = 0; i < seenCount - 1; i++)
                                selected[i] = i;
                            selected[seenCount - 1] = last;
                            int subsetIndex = choose[seenCount][last];
                            while (true)
                            {
                                Visit(offersMade, seenCount, subsetIndex++, local);
                                int changed = 0;
                                while (changed < seenCount - 1 && selected[changed] + 1 == selected[changed + 1])
                                    changed++;
                                if (changed == seenCount - 1)
                                    break;
                                selected[changed]++;
                                for (int i = 0; i < changed; i++)
                                    selected[i] = i;
                            }

                            return local;
                        }, local =>
                        {
                            lock (gate)
                                totals.Add(local);
                        });
                    }
                    else
                    {
                        var selected = totals.Selected;
                        for (int i = 0; i < seenCount; i++)
                            selected[i] = i;
                        int subsetIndex = 0;
                        while (true)
                        {
                            Visit(offersMade, seenCount, subsetIndex++, totals);
                            int changed = 0;
                            while (changed < seenCount && selected[changed] + 1 == (changed + 1 < seenCount ? selected[changed + 1] : effectCount))
                                changed++;
                            if (changed == seenCount)
                                break;
                            selected[changed]++;
                            for (int i = 0; i < changed; i++)
                                selected[i] = i;
                        }
                    }
                }

                var old = unseenMass;
                unseenMass = nextUnseenMass;
                nextUnseenMass = old;
                old = duplicateMass;
                duplicateMass = nextDuplicateMass;
                nextDuplicateMass = old;
                unseenMass[0] = duplicateMass[0] = 0;
            }

            if (choices == 2)
                Finish(0, 0, 1, 0, totals);
            var hits = totals.Hits;
            var aggregate = totals.Aggregate;
            var excluded = totals.Excluded;
            var negativeAggregate = totals.NegativeAggregate;
            var negativeExcluded = totals.NegativeExcluded;
            var coefficients = totals.Coefficients;
            var negatives = new double[effectCount];
            for (int removedMask = 0; removedMask < removalStateCount; removedMask++)
                for (int i = 0; i < effectCount; i++)
                {
                    double probability = selectionProbabilities[removedMask][i];
                    hits[i] += probability * (aggregate[removedMask] + negativeExcluded[removedMask * effectCount + i]);
                    negatives[i] += probability * (negativeAggregate[removedMask] + excluded[removedMask * effectCount + i]);
                    if (removalBits[i] != 0)
                        continue;
                    double power = probability * probability;
                    for (int degree = 0; degree < 6; degree++)
                    {
                        negatives[i] += power * coefficients[removedMask * 6 + degree];
                        power *= probability;
                    }
                }

            // Every transition is a dyadic ticket probability. Duplicate/null sums are exact binary additions.
            // The forward masses and the separated positive/negative finish circuits contain only + and *.
            // A monomial's rounded factors are bounded by gamma(L): offer paths <100*choices operations,
            // moments <4*effectCount, final sums <32*removalStateCount, and at most (removableCount+choices+4) additions per visited
            // subset into any accumulated result. Multiplying a mass by an input polynomial adds these path lengths;
            // it never multiplies two independently accumulated masses. No supported six-offer path can underflow:
            // its smallest positive probability is at least 2^(-23*36), above the smallest normal double.
            long operations = checked((removableCount + choices + 4L) * totals.VisitedStates + 100L * choices + 32L * removalStateCount + 4L * effectCount);
            const double unit = 1.1102230246251565e-16;
            double product = operations * unit;
            double gamma = Up(product / Down(1 - product));
            var result = new Bounds[effectCount];
            for (int i = 0; i < effectCount; i++)
            {
                double low = Down(Down(hits[i] / Up(1 + gamma)) - Up(negatives[i] / Down(1 - gamma)));
                double high = Up(Up(hits[i] / Down(1 - gamma)) - Down(negatives[i] / Up(1 + gamma)));
                result[i] = new Bounds(Math.Max(0, low), Math.Min(1, high));
            }

            return result;
        }
    }
}
