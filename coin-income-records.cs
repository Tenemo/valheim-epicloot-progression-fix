using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace EpicLootProgressionFix
{
    internal sealed class CoinIncomeEvent
    {
        public string Id, At, Currency, Kind, Source, Reference;
        public long World, Character;
        public int Amount;
    }

    internal sealed class CoinIncomeRecord
    {
        public int Version = 1;
        public string Account, Player, Received;
        public CoinIncomeEvent Event;
    }

    internal static class CoinIncomeRules
    {
        internal static bool Currency(string name) => name == "Coins" || name == "GoldBountyToken";
        internal static int Pending(int stack, bool pickedUp, string saved)
        {
            if (saved == null) return pickedUp ? 0 : Math.Max(0, stack);
            if (!int.TryParse(saved, NumberStyles.None, CultureInfo.InvariantCulture, out int amount) || amount < 0)
                throw new InvalidDataException("Invalid uncredited currency quantity.");
            return Math.Min(Math.Max(0, stack), amount);
        }
        internal static void Validate(CoinIncomeEvent item)
        {
            if (item == null || !Guid.TryParseExact(item.Id, "N", out _) || item.World == 0 || item.Character == 0 ||
                !Currency(item.Currency) || item.Amount <= 0 || item.Source == null || item.Source.Length > 80 ||
                (item.Reference?.Length ?? 0) > 240 ||
                !new[] { "income", "transfer-in", "transfer-out", "refund", "spend", "opening", "unclassified", "shortfall" }.Contains(item.Kind) ||
                !DateTime.TryParseExact(item.At, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) || date.Kind != DateTimeKind.Utc)
                throw new InvalidDataException("Invalid private currency event.");
        }
        internal static void ValidateIdentity(CoinIncomeEvent item, long world, long character)
        {
            Validate(item);
            if (item.World != world || item.Character != character)
                throw new InvalidDataException("Currency event belongs to another world or character.");
        }
    }

    // Separate from characters: restoring a character cannot erase the server's recorded income.
    internal sealed class CoinIncomeJournal
    {
        private readonly string path;
        private readonly Dictionary<string, CoinIncomeRecord> seen = new Dictionary<string, CoinIncomeRecord>();
        internal CoinIncomeJournal(string path)
        {
            this.path = path;
            if (!File.Exists(path)) return;
            byte[] bytes = File.ReadAllBytes(path);
            int complete = Array.LastIndexOf(bytes, (byte)'\n') + 1;
            if (complete != bytes.Length)
            {
                string recovery = path + ".incomplete-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(recovery, bytes);
                WholeFile.Replace(path, bytes.Take(complete).ToArray());
            }
            foreach (string line in Encoding.UTF8.GetString(bytes, 0, complete).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var record = JsonConvert.DeserializeObject<CoinIncomeRecord>(line);
                if (record == null || record.Version != 1 || string.IsNullOrEmpty(record.Account))
                    throw new InvalidDataException("Invalid private currency journal record.");
                CoinIncomeRules.Validate(record.Event);
                string key = record.Account + ":" + record.Event.Id;
                if (seen.ContainsKey(key)) throw new InvalidDataException("Duplicate currency journal record.");
                seen.Add(key, record);
            }
        }

        // One append for a client's whole batch. Replayed events are checked and skipped.
        internal void Append(string account, string player, IEnumerable<CoinIncomeEvent> items)
        {
            var records = new List<(string Key, CoinIncomeRecord Record)>();
            foreach (var item in items)
            {
                CoinIncomeRules.Validate(item);
                string key = account + ":" + item.Id;
                if (seen.TryGetValue(key, out var previous) || records.Any(entry => entry.Key == key))
                {
                    if (JsonConvert.SerializeObject(previous?.Event ?? records.First(entry => entry.Key == key).Record.Event) != JsonConvert.SerializeObject(item))
                        throw new InvalidDataException("Replayed currency event changed its contents.");
                    continue;
                }
                records.Add((key, new CoinIncomeRecord { Account = account, Player = player,
                    Received = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), Event = item }));
            }
            if (records.Count == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] bytes = Encoding.UTF8.GetBytes(string.Concat(records.Select(entry => JsonConvert.SerializeObject(entry.Record) + "\n")));
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                stream.Write(bytes, 0, bytes.Length);
            foreach (var entry in records) seen.Add(entry.Key, entry.Record);
        }
    }

    // Saved without waiting for the disk, and only after a change, so currency pickups never stall a frame.
    internal sealed class CoinIncomeOutbox
    {
        private readonly string path;
        private bool changed = true;
        internal List<CoinIncomeEvent> Pending { get; private set; }
        internal CoinIncomeOutbox(string path)
        {
            this.path = path;
            Pending = File.Exists(path) ? JsonConvert.DeserializeObject<List<CoinIncomeEvent>>(File.ReadAllText(path)) : new List<CoinIncomeEvent>();
            if (Pending == null) throw new InvalidDataException("Invalid private currency outbox.");
            foreach (var item in Pending) CoinIncomeRules.Validate(item);
        }
        internal void Add(CoinIncomeEvent item)
        {
            CoinIncomeRules.Validate(item);
            Pending.Add(item);
            changed = true;
        }
        internal void Save()
        {
            if (!changed) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WholeFile.Replace(path, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Pending)), false);
            changed = false;
        }
        internal void Acknowledge(IEnumerable<string> ids)
        {
            var accepted = new HashSet<string>(ids);
            changed |= Pending.RemoveAll(item => accepted.Contains(item.Id)) > 0;
        }
    }
}
