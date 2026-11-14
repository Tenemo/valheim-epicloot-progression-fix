using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

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
            return Through(cumulative, total, (double)cumulative / total / AugmentProbabilities.TicketScale);
        }

        // Any nonnegative estimate gives the same result: the native boundary is monotone in the ticket.
        private static int Through(float cumulative, float total, double estimate)
        {
            int index = estimate < AugmentProbabilities.TicketCount ? (int)estimate + 1 : AugmentProbabilities.TicketCount;
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

        // A state packs its blocked mask and, above the branch's effect bits, its trigger context. Every state
        // after the first blocks at least the effect it chose, so a zero key marks an empty slot.
        private struct Entry
        {
            internal ulong Key;
            internal double Mass;
        }

        private static ulong Hash(ulong key)
        {
            key = (key ^ key >> 30) * 0xBF58476D1CE4E5B9UL;
            key = (key ^ key >> 27) * 0x94D049BB133111EBUL;
            return key ^ key >> 31;
        }

        // Open addressing. Slots come from the high hash bits, so children inserted in hash order sweep
        // the table in address order.
        private sealed class Table
        {
            internal Entry[] Entries = new Entry[64];
            internal int Count, Bits = 6;

            // Reserved before a merge: hash-ordered inserts into a table that grows midway would pile the
            // early keys into its first slots.
            internal void Reserve(long count)
            {
                while (count * 4 > Entries.Length * 3L) Grow();
            }

            internal void Add(ulong key, ulong hash, double mass)
            {
                if ((Count + 1) * 4L > Entries.Length * 3L) Grow();
                var entries = Entries;
                int mask = entries.Length - 1, slot = (int)(hash >> (64 - Bits));
                while (true)
                {
                    ref var entry = ref entries[slot];
                    if (entry.Key == key) { entry.Mass += mass; return; }
                    if (entry.Key == 0) { entry.Key = key; entry.Mass = mass; Count++; return; }
                    slot = (slot + 1) & mask;
                }
            }

            private void Grow()
            {
                var old = Entries;
                Entries = new Entry[old.Length * 2]; Bits++; Count = 0;
                foreach (var entry in old) if (entry.Key != 0) Add(entry.Key, Hash(entry.Key), entry.Mass);
            }

            internal void Clear()
            {
                if (Count > 0) Array.Clear(Entries, 0, Entries.Length);
                Count = 0;
            }
        }

        private const ulong DeBruijn = 0x03F79D71B4CB0A89UL;
        private static readonly int[] BitIndex = { 0, 1, 48, 2, 57, 49, 28, 3, 61, 58, 50, 42, 38, 29, 17, 4, 62, 55, 59, 36, 53, 51, 43, 22, 45, 39, 33, 30, 24, 18, 12, 5,
            63, 47, 56, 27, 60, 41, 37, 16, 54, 35, 52, 21, 44, 32, 23, 11, 46, 26, 40, 15, 34, 20, 31, 10, 25, 14, 19, 9, 13, 8, 7, 6 };
        private static int Lowest(ulong bits) => BitIndex[(bits & (ulong)-(long)bits) * DeBruijn >> 58];

        private sealed class Step
        {
            internal double[] Hits, Failed;
            internal double Missing;
            internal long Operations;
        }

        // Children a worker found, grouped by the shard that stores them.
        private sealed class Output
        {
            internal readonly Entry[][] Items;
            internal readonly int[] Counts;
            internal Output(int shards)
            {
                Items = Enumerable.Range(0, shards).Select(_ => new Entry[64]).ToArray();
                Counts = new int[shards];
            }
            internal void Add(int shard, ulong key, double mass)
            {
                int index = Counts[shard]++;
                if (index == Items[shard].Length) Array.Resize(ref Items[shard], index * 2);
                Items[shard][index] = new Entry { Key = key, Mass = mass };
            }
        }

        // Masses are whole native tickets: an entry at depth d carries 2^(23d) times its probability, so a
        // transition is one product. Follows the states in entries[start, end) and queues their children.
        private static void Expand(Branch branch, Entry[] entries, int start, int end, Step step, Output output, int shards, CancellationToken cancellation)
        {
            var weights = branch.Weights; var exclusions = branch.Exclusions; var triggerBits = branch.TriggerBits; var enabled = branch.Enabled;
            int count = weights.Length;
            ulong blockedMask = count == 64 ? ulong.MaxValue : (1UL << count) - 1;
            var hits = step.Hits; var failed = step.Failed;
            double missing = 0;
            long operations = 0;
            int visited = 0;
            for (int e = start; e < end; e++)
            {
                double mass = entries[e].Mass;
                if (mass == 0) continue;
                if ((visited++ & 255) == 0) cancellation.ThrowIfCancellationRequested();
                ulong key = entries[e].Key, blocked = key & blockedMask;
                int context = enabled.Length == 1 ? 0 : (int)(key >> count);
                ulong available = enabled[context] & ~blocked;
                operations += 8;
                if (available == 0) continue;
                // The native pool order, double sum and float running total, exactly as Transitions.
                double sum = 0;
                for (ulong bits = available; bits != 0; bits &= bits - 1) sum += weights[Lowest(bits)];
                float total = (float)sum, cumulative = 0;
                double inverse = 1 / ((double)total * AugmentProbabilities.TicketScale);
                int previous = 0;
                for (ulong bits = available; bits != 0; bits &= bits - 1)
                {
                    int i = Lowest(bits);
                    cumulative = (float)(cumulative + weights[i]);
                    int through = total > 0 ? Through(cumulative, total, cumulative * inverse) : AugmentProbabilities.TicketsAtOrBelow(cumulative, total);
                    int tickets = through - previous;
                    previous = through;
                    if (tickets == 0) continue;
                    double following = mass * tickets;
                    hits[i] += following;
                    operations += 8;
                    if (output == null) continue;
                    ulong child = key | exclusions[i] | (ulong)triggerBits[i] << count;
                    output.Add((int)Hash(child) & (shards - 1), child, following);
                }
                int lost = AugmentProbabilities.TicketCount - previous;
                if (lost == 0) continue;
                // A null ticket ends the roll unsaved, so it takes back every effect this state had blocked.
                double failure = mass * lost;
                missing += failure;
                for (ulong bits = blocked; bits != 0; bits &= bits - 1) { failed[Lowest(bits)] += failure; operations += 8; }
            }
            step.Missing += missing;
            step.Operations += operations;
        }

        private const int SortBits = 12;
        private sealed class Sorter
        {
            internal Entry[] Items = new Entry[64];
            internal readonly int[] Starts = new int[(1 << SortBits) + 1];
        }

        // Each shard merges the children every worker queued for it. A stable counting sort on the high hash
        // bits keeps each state's additions in queue order while the inserts move through the table in order.
        private static void Merge(Table target, Output[] outputs, int shard, Sorter sorter)
        {
            int total = 0;
            foreach (var output in outputs) total += output.Counts[shard];
            if (sorter.Items.Length < total) sorter.Items = new Entry[Math.Max(total, sorter.Items.Length * 2)];
            var sorted = sorter.Items; var starts = sorter.Starts;
            Array.Clear(starts, 0, starts.Length);
            foreach (var output in outputs)
            {
                var items = output.Items[shard];
                for (int k = 0; k < output.Counts[shard]; k++) starts[(int)(Hash(items[k].Key) >> (64 - SortBits)) + 1]++;
            }
            for (int bucket = 1; bucket < starts.Length; bucket++) starts[bucket] += starts[bucket - 1];
            foreach (var output in outputs)
            {
                var items = output.Items[shard];
                for (int k = 0; k < output.Counts[shard]; k++) sorted[starts[(int)(Hash(items[k].Key) >> (64 - SortBits))]++] = items[k];
                output.Counts[shard] = 0;
            }
            target.Reserve((long)target.Count + total);
            for (int k = 0; k < total; k++) target.Add(sorted[k].Key, Hash(sorted[k].Key), sorted[k].Mass);
        }

        // A power of two, at most half the logical processors, which leaves the rest to the game. Workers keep
        // normal priority: a collection waits for every managed thread, and a starved one stalls the game.
        internal static int Workers = DefaultWorkers();
        private static int DefaultWorkers()
        {
            int workers = 1;
            while (workers * 2 <= Math.Min(16, Environment.ProcessorCount / 2)) workers *= 2;
            return workers;
        }

        // Queued children stay near this many per round, which bounds the queues and the reserve.
        private const long RoundTransitions = 1 << 19;

        internal static Bounds[] CalculateBounds(Input input, CancellationToken cancellation = default)
        {
            var result = new Bounds[input.EffectCount];
            int workers = Workers;
            if (workers < 1 || (workers & (workers - 1)) != 0) throw new InvalidOperationException("Enchant workers must be a power of two.");
            var options = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellation };
            var outputs = Enumerable.Range(0, workers).Select(_ => new Output(workers)).ToArray();
            var sorters = Enumerable.Range(0, workers).Select(_ => new Sorter()).ToArray();
            var states = Enumerable.Range(0, workers).Select(_ => new Table()).ToArray();
            var next = Enumerable.Range(0, workers).Select(_ => new Table()).ToArray();
            var origin = new[] { new Entry { Mass = 1 } };
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
                var guaranteedOutputs = new bool[input.EffectCount];
                foreach (int guaranteed in branch.Guaranteed) guaranteedOutputs[guaranteed] = true;
                var tails = Tails(branch.Counts).Select(Point).ToArray();
                int count = branch.Weights.Length;
                long operations = 16 + tails.Length * 4;
                var failed = new double[input.EffectCount];
                // Only its own pick blocks an effect that no other effect excludes, so its lost mass is exact.
                ulong excludedByOthers = 0;
                for (int i = 0; i < count; i++) excludedByOthers |= branch.Exclusions[i] & ~(1UL << i);
                var exactLoss = new bool[input.EffectCount];
                for (int i = 0; i < count; i++) exactLoss[branch.OutputIndices[i]] = (excludedByOthers & 1UL << i) == 0;
                var parts = Enumerable.Range(0, workers).Select(_ => new Step { Hits = new double[count], Failed = new double[count] }).ToArray();
                // Ticket units at the current depth: an exact power of two.
                double unit = 1;
                for (int step = 0; step < tails.Length; step++)
                {
                    unit /= AugmentProbabilities.TicketCount;
                    foreach (var part in parts) { Array.Clear(part.Hits, 0, count); Array.Clear(part.Failed, 0, count); part.Missing = 0; part.Operations = 0; }
                    var current = step == 0 ? new[] { origin } : states.Select(table => table.Entries).ToArray();
                    bool last = step + 1 == tails.Length;
                    long parents = step == 0 ? 1 : states.Sum(table => (long)table.Count);
                    void Run(Action<int> action)
                    {
                        if (workers == 1 || parents < 1024) for (int worker = 0; worker < workers; worker++) action(worker);
                        else
                            try { Parallel.For(0, workers, options, action); }
                            // Report a worker's own error, not an aggregate whose message names none.
                            catch (AggregateException error) { ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions[0]).Throw(); }
                    }
                    // Each worker follows the states of its own shard, a slice per round.
                    int rounds = last ? 1 : (int)(1 + parents * count / RoundTransitions);
                    for (int round = 0; round < rounds; round++)
                    {
                        Run(worker =>
                        {
                            if (worker >= current.Length) return;
                            var entries = current[worker];
                            Expand(branch, entries, (int)((long)entries.Length * round / rounds), (int)((long)entries.Length * (round + 1) / rounds),
                                parts[worker], last ? null : outputs[worker], workers, cancellation);
                        });
                        if (!last) Run(shard => Merge(next[shard], outputs, shard, sorters[shard]));
                    }
                    double scale = unit * tails[step] * weight, missing = 0;
                    for (int i = 0; i < count; i++)
                    {
                        double sum = 0, lost = 0;
                        foreach (var part in parts) { sum += part.Hits[i]; lost += part.Failed[i]; }
                        int index = branch.OutputIndices[i];
                        if (!guaranteedOutputs[index]) hits[index] += sum * scale;
                        failed[index] += lost * scale;
                    }
                    foreach (var part in parts) { missing += part.Missing; operations += part.Operations; }
                    failures += missing * scale;
                    operations += 8L * count * (workers + 4) + 8L * workers + 16;
                    foreach (var table in states) table.Clear();
                    var swap = states; states = next; next = swap;
                }
                foreach (var table in states) table.Clear();
                double error = Up(operations * 1.1102230246251565e-16);
                if (error >= .25) throw new InvalidOperationException("Enchant arithmetic operation bound exceeded.");
                double gamma = Up(error / Down(1 - error));
                double lowerScale = Up(1 + gamma), upperScale = Down(1 - gamma);
                // A null native ticket throws before saving. Guaranteed effects lose all failed mass. A random
                // effect loses the failed mass of states that picked it: exactly the mass of states that
                // blocked it when only its own pick can, and at most that otherwise. Ambiguous displayed
                // rounding is resolved by the exact marked-effect calculation.
                for (int i = 0; i < hits.Length; i++)
                {
                    double lost = guaranteedOutputs[i] ? failures : failed[i];
                    double high = guaranteedOutputs[i] || exactLoss[i] ? Up(Up(hits[i] / upperScale) - Down(lost / lowerScale)) : Up(hits[i] / upperScale);
                    result[i] += new Bounds(Down(Down(hits[i] / lowerScale) - Up(lost / upperScale)), high);
                }
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
            int contexts = branch.Enabled.Length, contextBits = 0;
            while (1 << contextBits < contexts) contextBits++;
            // A state's key holds the effect bits and, above them, the trigger context.
            if (count + contextBits > 64 || contexts != 1 << contextBits || branch.Exclusions.Length != count || branch.TriggerBits.Length != count || branch.OutputIndices.Length != count)
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
