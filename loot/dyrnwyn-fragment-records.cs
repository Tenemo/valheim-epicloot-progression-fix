using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EpicLootProgressionFix
{
    // Which accounts got each Dyrnwyn fragment in one world: the tip and blade from their pickups, the hilt dropped for them by the champion.
    internal sealed class DyrnwynFragmentRecords
    {
        internal const string Tip = "DyrnwynTipFragment", Blade = "DyrnwynBladeFragment", Hilt = "DyrnwynHiltFragment";
        internal static readonly string[] Fragments = { Tip, Blade, Hilt };

        private sealed class Saved
        {
            [JsonProperty(Required = Required.Always)] public int Version = 1;
            [JsonProperty(Required = Required.Always)] public Dictionary<string, string[]> Taken = new();
        }

        private readonly Dictionary<string, SortedSet<string>> taken =
            Fragments.ToDictionary(fragment => fragment, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

        internal bool Has(string fragment, string account) => Accounts(fragment).Contains(Valid(account));

        // False when the account already got this fragment.
        internal bool Add(string fragment, string account) => Accounts(fragment).Add(Valid(account));

        internal int Count(string fragment) => Accounts(fragment).Count;

        private SortedSet<string> Accounts(string fragment) =>
            fragment != null && taken.TryGetValue(fragment, out var accounts) ? accounts : throw new ArgumentException("Not a Dyrnwyn fragment: " + fragment);

        private static string Valid(string account) =>
            string.IsNullOrWhiteSpace(account) || account.Length > 128 ? throw new ArgumentException("Invalid account.") : account;

        internal string Serialize() => JsonConvert.SerializeObject(new Saved {
            Taken = taken.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal) }, Formatting.Indented);

        internal static DyrnwynFragmentRecords Parse(string json)
        {
            var saved = JsonConvert.DeserializeObject<Saved>(json);
            if (saved == null || saved.Version != 1 || saved.Taken == null || saved.Taken.Keys.Any(key => !Fragments.Contains(key)))
                throw new FormatException("Invalid Dyrnwyn fragment records.");
            var records = new DyrnwynFragmentRecords();
            foreach (var entry in saved.Taken)
            foreach (string account in entry.Value ?? throw new FormatException("Invalid Dyrnwyn fragment records."))
                records.Add(entry.Key, account);
            return records;
        }

        // The item journal names where each picked-up item first came from: "harvest <pickup>" for the tip and blade and
        // "creature <champion>" for the hilt. A fragment withdrawn from a chest or handed over has no such origin, but one
        // dropped on the ground keeps it, so only the first account that picked up each fragment kind from its source counts.
        // Records are seeded once, when a world has none.
        internal static DyrnwynFragmentRecords FromJournal(IEnumerable<string> lines, out int unreadable)
        {
            unreadable = 0;
            var first = new Dictionary<string, (DateTime At, string Account)>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JObject.Load(new JsonTextReader(new System.IO.StringReader(line)) { DateParseHandling = DateParseHandling.None });
                    var item = record["Event"] as JObject;
                    string fragment = (string)item?["Item"], origin = (string)item?["Origin"], account = (string)record["Account"];
                    if ((int?)record["Version"] != 1 || (string)item?["Action"] != "pickup" || !Fragments.Contains(fragment) ||
                        string.IsNullOrWhiteSpace(account) || origin == null ||
                        !origin.StartsWith(fragment == Hilt ? "creature " : "harvest ", StringComparison.Ordinal)) continue;
                    var at = DateTime.Parse((string)item["At"], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                    if (!first.TryGetValue(fragment, out var earlier) || at < earlier.At) first[fragment] = (at, account);
                }
                catch (Exception error) when (error is JsonException || error is FormatException || error is InvalidCastException ||
                    error is ArgumentException) { unreadable++; }
            }
            var records = new DyrnwynFragmentRecords();
            foreach (var entry in first) records.Add(entry.Key, entry.Value.Account);
            return records;
        }
    }
}
