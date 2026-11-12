using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace EpicLootProgressionFix
{
    // The state is the union of exclusions, plus prerequisites actually selected. Different rolls may
    // merge only when they leave exactly the same future pools. Native pool order is never rearranged.
    internal static class EnchantProbabilities
    {
        internal readonly struct Fraction
        {
            internal readonly BigInteger Numerator;
            internal readonly int Bits;
            internal Fraction(BigInteger numerator, int bits = 0) { Numerator = numerator; Bits = bits; }
            public static Fraction operator +(Fraction a, Fraction b)
            {
                int bits = Math.Max(a.Bits, b.Bits);
                return new Fraction((a.Numerator << (bits - a.Bits)) + (b.Numerator << (bits - b.Bits)), bits);
            }
            public static Fraction operator *(Fraction a, Fraction b) => new Fraction(a.Numerator * b.Numerator, a.Bits + b.Bits);
        }

        internal readonly struct Bounds
        {
            internal readonly double Low, High;
            internal Bounds(double low, double high) { Low = low; High = high; }
            public static Bounds operator +(Bounds a, Bounds b) => new Bounds(Down(a.Low + b.Low), Up(a.High + b.High));
        }
        private static double Down(double value) => value <= 0 ? 0 : BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) - 1);
        private static double Up(double value) => value == 0 ? 0 : BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) + 1);

        internal sealed class Branch
        {
            internal float[] Weights;
            internal ulong[] Exclusions, Enabled;
            internal int[] TriggerBits, OutputIndices, Guaranteed;
            internal Fraction Weight;
            // Index is the number of random effects, after guarantees have consumed their slots.
            internal Fraction[] Counts;
        }
        internal sealed class Input
        {
            internal int EffectCount;
            internal Branch[] Branches;
        }
        private readonly struct State : IEquatable<State>
        {
            internal readonly ulong Blocked;
            internal readonly int Triggers;
            internal State(ulong blocked, int triggers) { Blocked = blocked; Triggers = triggers; }
            public bool Equals(State other) => Blocked == other.Blocked && Triggers == other.Triggers;
            public override bool Equals(object other) => other is State value && Equals(value);
            public override int GetHashCode() => unchecked((int)Blocked * 397 ^ (int)(Blocked >> 32) ^ Triggers * 7919);
        }

        // Invert the verified native sampler, then correct at its actual float boundaries. The estimate
        // is only an optimization: the two comparisons certify the returned integer ticket boundary.
        internal static int Through(float cumulative, float total)
        {
            if (total <= 0) return AugmentProbabilities.TicketsAtOrBelow(cumulative, total);
            double estimate = (double)cumulative / total / AugmentProbabilities.TicketScale;
            int index = (int)Math.Max(0, Math.Min(AugmentProbabilities.TicketCount, Math.Floor(estimate) + 1));
            while (index > 0 && (float)(AugmentProbabilities.TicketValue(index - 1) * total) > cumulative) index--;
            while (index < AugmentProbabilities.TicketCount && (float)(AugmentProbabilities.TicketValue(index) * total) <= cumulative) index++;
            return index;
        }

        internal static int[] Tickets(float[] weights)
        {
            if (weights.Any(w => float.IsNaN(w) || float.IsInfinity(w) || w < 0))
                throw new NotSupportedException("Enchant selection weights must be finite and nonnegative.");
            float total = (float)weights.Sum(w => (double)w), cumulative = 0;
            if (float.IsInfinity(total)) throw new NotSupportedException("Enchant total selection weight is not finite.");
            int previous = 0;
            var result = new int[weights.Length + 1];
            for (int i = 0; i < weights.Length; i++)
            {
                cumulative = (float)(cumulative + weights[i]);
                int through = Through(cumulative, total);
                result[i] = through - previous; previous = through;
            }
            result[weights.Length] = AugmentProbabilities.TicketCount - previous;
            return result;
        }

        internal static int[] Calculate(Input input, CancellationToken cancellation = default)
        {
            // Keep subnormal arithmetic out of the relative-error proof. Extreme custom counts still
            // have an exact path; normal pack rolls are hundreds of binary places above this boundary.
            if (input.Branches.Any(b => b.Weight.Bits + b.Counts.Max(p => p.Bits) + 23L * Math.Min(b.Counts.Length - 1, b.Weights.Length) > 900))
                return Enumerable.Range(0, input.EffectCount).Select(i => { var p = ExactEffect(input, i, cancellation); return AugmentProbabilityExact.Round(p.Numerator, p.Bits); }).ToArray();
            var bounds = CalculateBounds(input, cancellation);
            var rounded = new int[input.EffectCount];
            for (int i = 0; i < rounded.Length; i++)
                if (!AugmentProbabilities.Rounded(new AugmentProbabilityIntervals.Bounds(bounds[i].Low, bounds[i].High), out rounded[i]))
                {
                    var exact = ExactEffect(input, i, cancellation);
                    rounded[i] = AugmentProbabilityExact.Round(exact.Numerator, exact.Bits);
                }
            return rounded;
        }

        internal static Bounds[] CalculateBounds(Input input, CancellationToken cancellation = default)
        {
            var result = new Bounds[input.EffectCount];
            foreach (var branch in input.Branches)
            {
                Validate(branch);
                // All mass operations are nonnegative. gamma(n) encloses the relative roundoff of any
                // path in this acyclic calculation when n bounds its arithmetic operations. Counting
                // the entire branch is deliberately conservative and avoids per-edge interval objects.
                double Point(Fraction value) => (double)value.Numerator * Math.Pow(2, -value.Bits);
                double weight = Point(branch.Weight), failures = 0;
                var hits = new double[input.EffectCount];
                foreach (int guaranteed in branch.Guaranteed.Distinct()) hits[guaranteed] = weight;
                var tails = Tails(branch.Counts).Select(Point).ToArray();
                long operations = 16 + tails.Length * 4;
                var states = new Dictionary<State, double> { [new State(0, 0)] = 1 };
                for (int step = 0; step < tails.Length; step++)
                {
                    var next = new Dictionary<State, double>();
                    int visited = 0;
                    foreach (var state in states)
                    {
                        if ((visited++ & 255) == 0) cancellation.ThrowIfCancellationRequested();
                        int missing = Transitions(branch, state.Key, (effect, tickets, following) =>
                        {
                            double probability = tickets / (double)AugmentProbabilities.TicketCount;
                            double mass = state.Value * probability;
                            int index = branch.OutputIndices[effect];
                            if (!branch.Guaranteed.Contains(index)) hits[index] += mass * tails[step] * weight;
                            if (step + 1 < tails.Length)
                            {
                                next.TryGetValue(following, out var prior);
                                next[following] = prior + mass;
                            }
                            operations += 8;
                        });
                        failures += state.Value * (missing / (double)AugmentProbabilities.TicketCount) * tails[step] * weight;
                        operations += 8;
                    }
                    states = next;
                }
                double error = Up(operations * 1.1102230246251565e-16);
                if (error >= .25) throw new InvalidOperationException("Enchant arithmetic operation bound exceeded.");
                double gamma = Up(error / Down(1 - error));
                double lowerScale = Up(1 + gamma), upperScale = Down(1 - gamma);
                double failedUpper = Up(failures / upperScale);
                // A null native ticket throws before saving. At most all failed mass carried any earlier
                // effect. Ambiguous displayed rounding is resolved by the exact marked-effect calculation.
                for (int i = 0; i < hits.Length; i++)
                    result[i] += new Bounds(Down(Down(hits[i] / lowerScale) - failedUpper), Up(hits[i] / upperScale));
            }
            return result;
        }

        internal static Fraction ExactEffect(Input input, int target, CancellationToken cancellation = default)
        {
            Fraction result = default;
            foreach (var branch in input.Branches)
            {
                Validate(branch);
                var tails = Tails(branch.Counts);
                bool guaranteed = branch.Guaranteed.Contains(target);
                var states = new Dictionary<(State State, bool Marked), BigInteger> { [(new State(0, 0), guaranteed)] = BigInteger.One };
                for (int step = 0; step <= tails.Length; step++)
                {
                    var next = new Dictionary<(State State, bool Marked), BigInteger>();
                    int visited = 0;
                    foreach (var state in states)
                    {
                        if ((visited++ & 255) == 0) cancellation.ThrowIfCancellationRequested();
                        bool empty = (branch.Enabled[state.Key.State.Triggers] & ~state.Key.State.Blocked) == 0;
                        if (state.Key.Marked)
                        {
                            var terminal = branch.Counts[step];
                            if (empty && step < tails.Length) terminal += tails[step];
                            result += new Fraction(state.Value, 23 * step) * terminal * branch.Weight;
                        }
                        if (empty || step == tails.Length) continue;
                        Transitions(branch, state.Key.State, (effect, tickets, following) =>
                        {
                            var mass = state.Value * tickets;
                            var key = (following, state.Key.Marked || branch.OutputIndices[effect] == target);
                            next.TryGetValue(key, out var prior);
                            next[key] = prior + mass;
                        });
                    }
                    states = next;
                }
            }
            return result;
        }

        private static Fraction[] Tails(Fraction[] counts)
        {
            var result = new Fraction[Math.Max(0, counts.Length - 1)];
            Fraction sum = default;
            for (int i = counts.Length - 1; i > 0; i--) { sum += counts[i]; result[i - 1] = sum; }
            return result;
        }

        private static void Validate(Branch branch)
        {
            int count = branch.Weights.Length;
            if (count > 64 || branch.Exclusions.Length != count || branch.TriggerBits.Length != count || branch.OutputIndices.Length != count)
                throw new NotSupportedException("Enchant effect snapshot has unsupported dimensions.");
            Tickets(branch.Weights);
            for (int i = 0; i < count; i++)
                if ((branch.Exclusions[i] & (1UL << i)) == 0)
                    throw new NotSupportedException("Enchant effects must exclude a second copy of themselves.");
        }

        private static int Transitions(Branch branch, State state, Action<int, int, State> visit)
        {
            ulong available = branch.Enabled[state.Triggers] & ~state.Blocked;
            if (available == 0) return 0;
            double sum = 0;
            for (int i = 0; i < branch.Weights.Length; i++)
                if ((available & (1UL << i)) != 0) sum += branch.Weights[i];
            float total = (float)sum, cumulative = 0;
            int previous = 0;
            for (int i = 0; i < branch.Weights.Length; i++)
            {
                if ((available & (1UL << i)) == 0) continue;
                cumulative = (float)(cumulative + branch.Weights[i]);
                int through = Through(cumulative, total), tickets = through - previous;
                previous = through;
                if (tickets != 0) visit(i, tickets, new State(state.Blocked | branch.Exclusions[i], state.Triggers | branch.TriggerBits[i]));
            }
            return AugmentProbabilities.TicketCount - previous;
        }
    }
}
