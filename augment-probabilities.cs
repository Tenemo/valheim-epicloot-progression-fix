using System;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace EpicLootProgressionFix
{
    // Appearance probabilities under uniform native tickets, never a prediction of the gameplay random state.
    internal static class AugmentProbabilities
    {
        internal const int TicketCount = 1 << 23;
        // Verified Unity Windows x64 Random.value: 23 masked bits multiplied by float 0x34000001.
        internal const float TicketScale = 1.1920930376163766e-7f;
        internal static float TicketValue(int ticket) => (float)(ticket * TicketScale);

        internal static int TicketsAtOrBelow(float cumulative, float total)
        {
            int low = 0, high = TicketCount;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if ((float)(TicketValue(middle) * total) <= cumulative) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private static double Down(double value) => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) - 1);
        private static double Up(double value) => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) + 1);

        internal static bool Rounded(AugmentProbabilityIntervals.Bounds probability, out int hundredths)
        {
            double low = probability.Low <= 0 ? 0 : Down(Down(probability.Low * 10000) + .5);
            double high = probability.High >= 1 ? 10000 : Up(Up(probability.High * 10000) + .5);
            hundredths = (int)Math.Floor(low);
            return hundredths == (int)Math.Floor(high);
        }

        internal static int[] Calculate(float[] weights, bool[] removable, int choices)
            => CalculateCancellable(weights, removable, choices, CancellationToken.None);

        internal static int[] CalculateCancellable(float[] weights, bool[] removable, int choices, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (weights.Length != removable.Length || choices < 0 ||
                weights.Any(weight => float.IsNaN(weight) || float.IsInfinity(weight) || weight <= 0))
                throw new ArgumentException("Augment weights must be finite and positive, with matching removal rules.");
            if (float.IsInfinity((float)weights.Sum(weight => (double)weight)))
                throw new ArgumentException("The native augment total weight is not finite.");
            if (weights.Length == 0 || choices == 0) return new int[weights.Length];
            // The native loop can never offer more than its initial pool size. This also bounds an unusually high table level.
            choices = Math.Min(choices, weights.Length);
            if (choices <= 6 && weights.Length <= 64 && removable.Count(value => value) <= 8)
            {
                var bounds = AugmentProbabilityIntervals.Calculate(weights, removable, choices, cancellation);
                var rounded = new int[weights.Length];
                bool certified = true;
                for (int i = 0; i < bounds.Length; i++) certified &= Rounded(bounds[i], out rounded[i]);
                if (certified) return rounded;
            }
            // Every native probability has a power-of-two denominator. Settle an unresolved rounding bucket exactly;
            // the same path handles table levels beyond the optimized six-choice calculation without reducing choices.
            var exact = AugmentProbabilityExact.Calculate(weights, removable, choices, cancellation);
            return exact.Numerators.Select(value => AugmentProbabilityExact.Round(value, exact.DenominatorBits)).ToArray();
        }

        internal static string Format(int hundredths) => (hundredths / 100m).ToString("0.00", CultureInfo.CurrentCulture) + "%";
    }
}
