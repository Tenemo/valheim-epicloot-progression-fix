using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace EpicLootProgressionFix
{
    // A feature that turns itself off on a game or mod build it does not recognize reports it here. The report reaches the
    // log as an error, and mod-compatibility fails on the pinned pack instead of the feature quietly disappearing.
    internal static class DisabledFeatures
    {
        private static readonly Dictionary<string, string> reasons = new Dictionary<string, string>();
        private static ManualLogSource logger;

        internal static string Summary => reasons.Count == 0 ? null : string.Join("\n", reasons.Select(pair => pair.Key + ": " + pair.Value));

        internal static void Initialize(ManualLogSource log) => logger = log;

        // A null reason means the feature works again.
        internal static void Set(string feature, string reason)
        {
            if (reason == null) { reasons.Remove(feature); return; }
            if (reasons.TryGetValue(feature, out var known) && known == reason) return;
            reasons[feature] = reason;
            logger?.LogError(feature + " turned off: " + reason);
        }
    }
}
