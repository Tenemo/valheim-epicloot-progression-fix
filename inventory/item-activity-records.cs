using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace EpicLootProgressionFix
{
    internal sealed class ItemActivityBatchLimit
    {
        private const double BurstCapacity = 10, BatchesPerSecond = 10;
        private double previousSeconds, availableBatches = BurstCapacity;

        internal bool TryAccept(double elapsedSeconds)
        {
            availableBatches = Math.Min(BurstCapacity, availableBatches + Math.Max(0, elapsedSeconds - previousSeconds) * BatchesPerSecond);
            previousSeconds = elapsedSeconds;
            if (availableBatches < 1) return false;
            availableBatches--;
            return true;
        }
    }

    internal sealed class ItemActivityEvent
    {
        public string Id, At, Action, Item, Tracking, Position, Biome, Source, Origin, Detail;
        public long World, Character;
        public int Count, Quality;
        public bool Begins;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public TreasureDiscovery Treasure;
    }

    // One treasure map EpicLoot marked found. The world, owner, biome and interval identify the map; a repeated delivery
    // keeps its event id. Owner 0 means the map's owner could not be confirmed, which records the find without credit.
    internal sealed class TreasureDiscovery
    {
        public int Schema = 1;
        public long Owner;
        public string Biome, Chest, Recorder;
        public int Interval;
    }

    internal sealed class ItemActivityRecord
    {
        public int Version = 1;
        public string Account, Player, Received;
        public ItemActivityEvent Event;
    }

    internal static class ItemActivityRules
    {
        internal const string Treasure = "treasure";
        internal static readonly string[] Actions = { "pickup", "withdraw", "deposit", "grave", "craft", "upgrade", "refine", "purchase", "bounty",
            "enchant", "begin", "unknown", "gap", "session-start", "session-end", Treasure };
        // Heightmap.Biome names; EpicLoot sells treasure maps by these.
        internal static readonly string[] TreasureBiomes = { "Meadows", "BlackForest", "Swamp", "Mountain", "Plains", "Ocean", "Mistlands", "AshLands", "DeepNorth" };
        private static readonly System.Text.RegularExpressions.Regex ChestId = new System.Text.RegularExpressions.Regex(@"^-?\d{1,20}:\d{1,10}$");
        internal static string Clip(string text, int length) => text == null || text.Length <= length ? text : text.Substring(0, length);
        internal static void Validate(ItemActivityEvent item)
        {
            if (item == null || !Guid.TryParseExact(item.Id, "N", out _) || item.World == 0 || item.Character == 0 || !Actions.Contains(item.Action) ||
                (item.Item?.Length ?? 0) > 80 || item.Count < 0 || item.Quality < 0 || (item.Tracking != null && !Guid.TryParseExact(item.Tracking, "N", out _)) ||
                (item.Position?.Length ?? 0) > 40 || (item.Biome?.Length ?? 0) > 20 || (item.Source?.Length ?? 0) > 120 ||
                (item.Origin?.Length ?? 0) > 160 || (item.Detail?.Length ?? 0) > 400 ||
                !DateTime.TryParseExact(item.At, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) || date.Kind != DateTimeKind.Utc)
                throw new InvalidDataException("Invalid item activity event.");
            var treasure = item.Treasure;
            if ((item.Action == Treasure) != (treasure != null) || treasure != null && (treasure.Schema != 1 || treasure.Owner != 0 && treasure.Owner != item.Character ||
                !TreasureBiomes.Contains(treasure.Biome) || treasure.Interval < 0 || treasure.Chest != null && !ChestId.IsMatch(treasure.Chest) ||
                string.IsNullOrEmpty(treasure.Recorder) || treasure.Recorder.Length > 20 || item.Count != 1 || item.Item != null || item.Tracking != null))
                throw new InvalidDataException("Invalid treasure discovery receipt.");
        }
        // Repeated finds of one map share this, whichever receipt reports them.
        internal static string TreasureIdentity(ItemActivityEvent item) =>
            $"{item.World}:{item.Treasure.Owner}:{item.Treasure.Biome}:{item.Treasure.Interval}";
        internal static void ValidateIdentity(ItemActivityEvent item, long world, long character)
        {
            Validate(item);
            if (item.World != world || item.Character != character)
                throw new InvalidDataException("Item activity event belongs to another world or character.");
        }
    }

    // Daily files named by the server's UTC receiving date. Replays of events from the last two days are recognized and
    // skipped without reading older days; the report also ignores a repeated account and event id.
    internal sealed class ItemActivityJournal
    {
        private readonly string directory;
        private readonly Dictionary<string, HashSet<string>> days = new Dictionary<string, HashSet<string>>();
        internal ItemActivityJournal(string directory) => this.directory = directory;

        private HashSet<string> Day(string day)
        {
            if (days.TryGetValue(day, out var seen)) return seen;
            seen = new HashSet<string>();
            string path = Path.Combine(directory, day + ".jsonl");
            if (File.Exists(path))
            {
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
                    var record = JsonConvert.DeserializeObject<ItemActivityRecord>(line);
                    if (record?.Event == null || record.Version != 1 || string.IsNullOrEmpty(record.Account))
                        throw new InvalidDataException("Invalid item activity journal record.");
                    seen.Add(record.Account + ":" + record.Event.Id);
                }
            }
            days[day] = seen;
            return seen;
        }

        // One append for a client's whole batch; returns the events written now.
        internal int Append(string account, string player, IEnumerable<ItemActivityEvent> items, DateTime now)
        {
            string today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), yesterday = now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (string old in days.Keys.Where(day => day != today && day != yesterday).ToList()) days.Remove(old);
            var current = Day(today);
            var previous = Day(yesterday);
            var keys = new HashSet<string>();
            var lines = new StringBuilder();
            bool critical = false;
            foreach (var item in items)
            {
                ItemActivityRules.Validate(item);
                string key = account + ":" + item.Id;
                if (current.Contains(key) || previous.Contains(key) || !keys.Add(key)) continue;
                critical |= item.Action == ItemActivityRules.Treasure;
                lines.Append(JsonConvert.SerializeObject(new ItemActivityRecord { Account = account, Player = player,
                    Received = now.ToString("O", CultureInfo.InvariantCulture), Event = item })).Append('\n');
            }
            if (keys.Count == 0) return 0;
            Directory.CreateDirectory(directory);
            byte[] bytes = Encoding.UTF8.GetBytes(lines.ToString());
            using (var stream = new FileStream(Path.Combine(directory, today + ".jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                // A treasure discovery is acknowledged only once it would survive a power loss.
                stream.Flush(critical);
            }
            current.UnionWith(keys);
            return keys.Count;
        }
    }

    // The client's unsent events, bounded. Events that do not fit are counted and reported as one gap once there is room.
    internal sealed class ItemActivityOutbox
    {
        internal readonly int Capacity;
        internal List<ItemActivityEvent> Pending { get; private set; }
        internal int Lost { get; private set; }
        internal bool Changed { get; private set; } = true;
        internal ItemActivityOutbox(string path, int capacity)
        {
            Capacity = capacity;
            Pending = File.Exists(path) ? JsonConvert.DeserializeObject<List<ItemActivityEvent>>(File.ReadAllText(path)) : new List<ItemActivityEvent>();
            if (Pending == null) throw new InvalidDataException("Invalid item activity outbox.");
            foreach (var item in Pending) ItemActivityRules.Validate(item);
        }
        internal bool Add(ItemActivityEvent item)
        {
            ItemActivityRules.Validate(item);
            if (Pending.Count >= Capacity) { Lost++; return false; }
            Pending.Add(item);
            Changed = true;
            return true;
        }
        internal int TakeLost() { int lost = Lost; Lost = 0; return lost; }
        internal List<ItemActivityEvent> Copy() { Changed = false; return new List<ItemActivityEvent>(Pending); }
        internal static void Save(string path, List<ItemActivityEvent> items)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WholeFile.Replace(path, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(items)), false);
        }
        internal void Acknowledge(IEnumerable<string> ids)
        {
            var accepted = new HashSet<string>(ids);
            Changed |= Pending.RemoveAll(item => accepted.Contains(item.Id)) > 0;
        }
    }

    // The client's unacknowledged treasure discoveries, apart from the bounded item backlog so a full backlog never drops
    // one. Every change is written through to disk before it returns, on the caller's thread, so no older copy can
    // overwrite a newer one. A failed write keeps the receipt in memory, still sent, until a later write succeeds.
    internal sealed class TreasureReceipts
    {
        private readonly string path;
        internal List<ItemActivityEvent> Pending { get; } = new List<ItemActivityEvent>();
        internal bool Unsaved { get; private set; }
        // An unreadable file is kept aside here and the set starts empty.
        internal string Unreadable { get; }

        internal TreasureReceipts(string path)
        {
            this.path = path;
            if (!File.Exists(path)) return;
            try
            {
                var saved = JsonConvert.DeserializeObject<List<ItemActivityEvent>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty treasure receipts.");
                foreach (var item in saved)
                    if (item?.Action != ItemActivityRules.Treasure) throw new InvalidDataException("Not a treasure receipt.");
                    else ItemActivityRules.Validate(item);
                Pending.AddRange(saved);
            }
            catch (Exception error) when (error is InvalidDataException || error is JsonException)
            {
                Unreadable = path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Move(path, Unreadable);
            }
        }

        // False when this map already waits for delivery, which keeps its first receipt.
        internal bool Add(ItemActivityEvent item)
        {
            ItemActivityRules.Validate(item);
            if (item.Action != ItemActivityRules.Treasure) throw new InvalidDataException("Not a treasure receipt.");
            string identity = ItemActivityRules.TreasureIdentity(item);
            if (Pending.Any(pending => ItemActivityRules.TreasureIdentity(pending) == identity)) return false;
            Pending.Add(item);
            Save();
            return true;
        }

        internal int Acknowledge(IEnumerable<string> ids)
        {
            var accepted = new HashSet<string>(ids);
            int removed = Pending.RemoveAll(item => accepted.Contains(item.Id));
            if (removed > 0) Save();
            return removed;
        }

        internal void Save()
        {
            Unsaved = true;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WholeFile.Replace(path, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Pending)));
            Unsaved = false;
        }
    }

    // When the server first ran treasure recording for this world: written once, beside the world's journal, and never
    // changed. Finds before it are the recovered history; receipts after it are counted from it.
    internal sealed class TreasureRecordingStart
    {
        internal const string FileName = "treasure-recording.json";
        public int Schema = 1;
        public long World;
        public string WorldName, Activated, Recorder;

        internal static TreasureRecordingStart Record(string directory, long world, string worldName, string recorder, DateTime now)
        {
            string path = Path.Combine(directory, FileName);
            if (File.Exists(path))
            {
                var saved = JsonConvert.DeserializeObject<TreasureRecordingStart>(File.ReadAllText(path));
                if (saved == null || saved.Schema != 1 || saved.World != world || string.IsNullOrEmpty(saved.Activated))
                    throw new InvalidDataException("The treasure recording start belongs to another world or is invalid.");
                return saved;
            }
            var started = new TreasureRecordingStart { World = world, WorldName = worldName, Recorder = recorder,
                Activated = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) };
            Directory.CreateDirectory(directory);
            WholeFile.Replace(path, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(started)));
            return started;
        }
    }

    // One background thread for the journal's file work, so pickups and crafting never wait for the disk. A full queue
    // refuses new work; the client then keeps its events and sends them again.
    internal sealed class ItemActivityWriter
    {
        private readonly BlockingCollection<Action> work;
        private readonly ConcurrentQueue<Exception> errors = new ConcurrentQueue<Exception>();
        internal ItemActivityWriter(int capacity)
        {
            work = new BlockingCollection<Action>(capacity);
            new Thread(() =>
            {
                foreach (var job in work.GetConsumingEnumerable())
                {
                    try { job(); }
                    catch (Exception error) { errors.Enqueue(error); }
                }
            }) { IsBackground = true, Name = "Item activity writer" }.Start();
        }
        internal bool Post(Action job) => work.TryAdd(job);
        internal bool TakeError(out Exception error) => errors.TryDequeue(out error);
    }
}
