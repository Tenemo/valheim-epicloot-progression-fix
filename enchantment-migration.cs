using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;
using EpicLoot;
using EpicLoot.ShardStones;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EpicLootProgressionFix
{
    // The one administrative repair of completely dead equipment affixes. A deployment leaves a request beside the saves;
    // the dedicated server runs it after the world loads and before it opens to players. Every character save
    // ServerCharacters serves and every item record the world keeps is read in its own saved format, and each completely
    // dead affix is rerolled in its slot from the ordinary loot pool at the item's rarity values, repeatably from the
    // item's original enchantment data. The repaired slot is an ordinary fresh affix without augment or temper marks;
    // every other affix, every socket and every rune and shardstone stays exactly as it was. The report goes where the
    // request names, outside the saves. A failed, blocked or unverified pass keeps the server closed.
    internal static class EnchantmentMigration
    {
        private static readonly byte[] KeyBytes = Encoding.UTF8.GetBytes(MagicItemData.Key);
        private static readonly AccessTools.FieldRef<PlayerProfile, byte[]> ProfileData = AccessTools.FieldRefAccess<PlayerProfile, byte[]>("m_playerData");
        private static readonly IDictionary SavedArrays = (IDictionary)AccessTools.Field(typeof(ZDOExtraData), "s_byteArrays").GetValue(null);
        private static readonly IDictionary SavedTexts = (IDictionary)AccessTools.Field(typeof(ZDOExtraData), "s_strings").GetValue(null);
        // Armor stands keep each native position's item under the position's own key.
        private static readonly Dictionary<int, int> StandPositions = Enumerable.Range(0, 64).ToDictionary(position => (position + "_itemData").GetStableHashCode());
        private static ManualLogSource log;

        internal static string RequestPath => Path.Combine(global::Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "enchantment-migration", "request.json");

        // Installed apart from the gameplay patches, so a request keeps the server closed even when those fail to load.
        internal static void Initialize(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            harmony.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.OpenServer)), prefix: new HarmonyMethod(typeof(EnchantmentMigration), nameof(Open)));
        }

        private static bool Open(ZNet __instance) => !__instance.IsDedicated() || !File.Exists(RequestPath) || Run();

        // One pass over everything the campaign saves: the changes it would make, what blocks it and what it keeps.
        private sealed class Pass
        {
            internal readonly JArray Repairs = new JArray(), Blockers = new JArray(), Unloadable = new JArray();
            internal readonly List<(string Path, byte[] Data)> Characters = new();
            internal readonly List<(ZDO Data, int Key, byte[] Value)> World = new();
            // Each enchanted item's sockets, and each rune's and shardstone's whole data, by location.
            internal readonly SortedDictionary<string, string> Kept = new(StringComparer.Ordinal);
            internal int CharacterSaves, Items, Enchanted, Inactive, UnusedCopies;
            internal void Block(string location, string problem) => Blockers.Add(location + ": " + problem);
        }

        // One saved item: its prefab, its quality and the extent of its enchantment value, if any, in its payload.
        private sealed class Saved { internal int Prefab, Quality = 1, Start = -1, End; internal string Magic; }

        private static bool Run()
        {
            var report = new JObject { ["started"] = DateTime.UtcNow.ToString("o") };
            string reportPath = null;
            var clock = Stopwatch.StartNew();
            bool open;
            try
            {
                var request = JObject.Parse(File.ReadAllText(RequestPath));
                string mode = (string)request["mode"];
                reportPath = (string)request["report"];
                if (mode != "dry-run" && mode != "apply" || string.IsNullOrEmpty(reportPath) || !Path.IsPathRooted(reportPath))
                    throw new InvalidDataException("The request needs the mode dry-run or apply and an absolute report path.");
                report["mode"] = mode;
                report["world"] = ZNet.instance.GetWorldName();
                if (request["world"] != null && (string)request["world"] != ZNet.instance.GetWorldName())
                    throw new InvalidDataException("The request is for the world " + request["world"] + ".");
                if (!Plugin.Ready) throw new InvalidOperationException("The gameplay patches did not load, so dead affixes cannot be recognized.");
                var before = Plan();
                Describe(report, before, "before");
                if (mode == "dry-run" || before.Blockers.Count > 0)
                {
                    report["status"] = before.Blockers.Count > 0 ? "blocked" : "planned";
                    open = mode == "dry-run";
                }
                else
                {
                    if (before.World.Count > 0 && (ZNet.m_loadError || ZoneSystem.instance.SkipSaving() || DungeonDB.instance.SkipSaving() ||
                        SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveWorld)))
                        throw new InvalidOperationException("The world cannot be saved now, so nothing was changed.");
                    foreach (var (path, data) in before.Characters)
                    {
                        string temporary = path + ".migrating";
                        File.WriteAllBytes(temporary, data);
                        File.Replace(temporary, path, null);
                    }
                    foreach (var (data, key, value) in before.World) data.Set(key, value);
                    if (before.World.Count > 0) ZNet.instance.Save(true);
                    // The characters are read again from their files; the world as it was saved.
                    var after = Plan();
                    Describe(report, after, "after");
                    var problems = new List<string>();
                    if (after.Characters.Count + after.World.Count > 0) problems.Add((after.Characters.Count + after.World.Count) + " saves still have dead affixes");
                    if (after.Blockers.Count > 0) problems.Add(after.Blockers.Count + " problems after the repair");
                    if (after.Enchanted != before.Enchanted) problems.Add("the enchanted item count changed from " + before.Enchanted + " to " + after.Enchanted);
                    if (Digest(after.Kept) != Digest(before.Kept)) problems.Add("sockets, runes or shardstones changed");
                    if (problems.Count > 0) throw new InvalidDataException("The repair did not verify: " + string.Join("; ", problems) + ".");
                    report["status"] = "applied";
                    open = true;
                }
                if (open) File.Delete(RequestPath);
            }
            catch (Exception error)
            {
                report["status"] = "failed";
                report["error"] = error.ToString();
                open = false;
            }
            report["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
            report["finished"] = DateTime.UtcNow.ToString("o");
            log.LogInfo($"Enchantment migration {report["mode"]}: {report["status"]}, {report["before"]?["repairs"]?.Count() ?? 0} dead affixes, " +
                $"{report["before"]?["blockers"]?.Count() ?? 0} problems, {report["seconds"]} s. Report: {reportPath}");
            if (!open) log.LogError("The server stays closed to players until the enchantment migration request completes: " + (report["error"] ?? report["status"]));
            try
            {
                if (reportPath == null) throw new InvalidDataException("The request names no report.");
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
                File.WriteAllText(reportPath + ".tmp", report.ToString(Formatting.Indented) + "\n");
                if (File.Exists(reportPath)) File.Replace(reportPath + ".tmp", reportPath, null);
                else File.Move(reportPath + ".tmp", reportPath);
            }
            catch (Exception error)
            {
                log.LogError("The enchantment migration report could not be written: " + error.Message);
                return false;
            }
            return open;
        }

        private static void Describe(JObject report, Pass pass, string name) => report[name] = new JObject
        {
            ["character_saves"] = pass.CharacterSaves, ["items"] = pass.Items, ["enchanted"] = pass.Enchanted, ["inactive_records"] = pass.Inactive,
            ["unused_copies"] = pass.UnusedCopies,
            ["changed_characters"] = pass.Characters.Count, ["changed_world_records"] = pass.World.Count,
            ["kept_digest"] = Digest(pass.Kept), ["repairs"] = pass.Repairs, ["blockers"] = pass.Blockers, ["unloadable"] = pass.Unloadable,
        };

        private static Pass Plan()
        {
            var pass = new Pass();
            PlanCharacters(pass);
            PlanWorld(pass);
            return pass;
        }

        private static void PlanCharacters(Pass pass)
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("org.bepinex.plugins.servercharacters"))
            {
                pass.Block("characters", "without ServerCharacters the character saves are not on this server");
                return;
            }
            string folder = SaveSystem.GetCharacterFolderPath(FileHelpers.FileSource.Local);
            foreach (string path in Directory.GetFiles(folder).OrderBy(path => path, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(path);
                // ServerCharacters' own choice of the saves it serves: <platform>_<id>_<name>.fch, without its backups.
                if (name.Split('_').Length < 3 || !name.EndsWith(".fch", StringComparison.Ordinal) || name.Contains("_backup_")) continue;
                pass.CharacterSaves++;
                try { PlanCharacter(pass, path, name); }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException)
                { pass.Block("character " + name, error.Message); }
            }
        }

        // A character file holds its payload's length, the payload, the hash's length and the payload's SHA-512 hash; the
        // payload ends with a flag and the character's data, which holds the inventory and the recalling weapons.
        private static void PlanCharacter(Pass pass, string path, string name)
        {
            byte[] file = File.ReadAllBytes(path);
            var profile = new PlayerProfile(name.Substring(0, name.Length - 4), FileHelpers.FileSource.Local);
            if (!profile.Load()) throw new InvalidDataException("the game cannot load it");
            byte[] data = ProfileData(profile) ?? throw new InvalidDataException("it holds no character");
            int length = BitConverter.ToInt32(file, 0);
            if (length < data.Length + 5 || file.Length < 8 + length || file.Length != 8 + length + BitConverter.ToInt32(file, 4 + length))
                throw new InvalidDataException("its file layout is not the game's");
            var payload = new byte[length];
            Array.Copy(file, 4, payload, 0, length);
            using (var sha = SHA512.Create())
                if (!sha.ComputeHash(payload).SequenceEqual(file.Skip(8 + length)))
                    throw new InvalidDataException("its hash does not match its data");
            int start = length - data.Length;
            if (payload[start - 5] != 1 || BitConverter.ToInt32(payload, start - 4) != data.Length || !payload.Skip(start).SequenceEqual(data))
                throw new InvalidDataException("its character data is not where the game keeps it");
            string location = "character " + name;
            var (items, custom) = ReadCharacter(data);
            var edits = RepairItems(pass, location + " inventory", items, item => item.Prefab);
            int found = items.Count(item => item.Start >= 0);
            foreach (var (key, value, valueStart, valueEnd) in custom)
            {
                if (key != RecallRecovery.StateKey)
                {
                    if (Holds(value)) pass.Block(location, "its custom data " + key + " holds enchanted items in a format this repair does not know");
                    continue;
                }
                byte[] recall;
                try { recall = Convert.FromBase64String(value); }
                catch (FormatException) { pass.Block(location, "its recalling weapons are unreadable"); continue; }
                var package = new ZPackage(recall);
                var held = ReadInventory(package);
                if (held == null || package.GetPos() != recall.Length || Occurrences(recall) != held.Count(item => item.Start >= 0))
                { pass.Block(location, "its recalling weapons are unreadable"); continue; }
                var recallEdits = RepairItems(pass, location + " recalling weapons", held, item => item.Prefab);
                if (recallEdits.Count > 0) edits.Add((valueStart, valueEnd, Encoded(Convert.ToBase64String(Splice(recall, recallEdits)))));
            }
            if (Occurrences(data) != found) pass.Block(location, "it holds enchanted items outside its inventory in a format this repair does not know");
            if (edits.Count == 0) return;
            byte[] repaired = Splice(data, edits);
            using var rebuilt = new MemoryStream();
            rebuilt.Write(payload, 0, start - 4);
            rebuilt.Write(BitConverter.GetBytes(repaired.Length), 0, 4);
            rebuilt.Write(repaired, 0, repaired.Length);
            byte[] newPayload = rebuilt.ToArray();
            using var output = new MemoryStream();
            byte[] hash;
            using (var sha = SHA512.Create()) hash = sha.ComputeHash(newPayload);
            output.Write(BitConverter.GetBytes(newPayload.Length), 0, 4);
            output.Write(newPayload, 0, newPayload.Length);
            output.Write(BitConverter.GetBytes(hash.Length), 0, 4);
            output.Write(hash, 0, hash.Length);
            pass.Characters.Add((path, output.ToArray()));
        }

        // The inventory and custom data of character data in the game's last two versions, read field by field. A character
        // that has not played since the last change keeps version 33, without the drop counters and upgrade seeds at the end.
        private static (List<Saved> Items, List<(string Key, string Value, int Start, int End)> Custom) ReadCharacter(byte[] data)
        {
            var package = new ZPackage(data);
            int version = package.ReadInt();
            if (version != (int)global::Version.PlayerData.ChunkedNorth && version != (int)global::Version.PlayerData.PostLaunch)
                throw new InvalidDataException("its character data version " + version + " is older than the game's last two");
            for (int index = 0; index < 4; index++) package.ReadSingle();
            package.ReadString(); package.ReadSingle();
            var items = ReadInventory(package) ?? throw new InvalidDataException("its inventory uses a format from before the current one");
            void Strings() { for (int count = package.ReadInt(); count > 0; count--) package.ReadString(); }
            Strings();
            for (int count = package.ReadInt(); count > 0; count--) { package.ReadString(); package.ReadInt(); }
            for (int index = 0; index < 5; index++) Strings();
            for (int count = package.ReadInt(); count > 0; count--) { package.ReadString(); package.ReadString(); }
            package.ReadString(); package.ReadString(); package.ReadVector3(); package.ReadVector3(); package.ReadInt();
            for (int count = package.ReadInt(); count > 0; count--) { package.ReadString(); package.ReadSingle(); }
            int skills = package.ReadInt();
            for (int count = package.ReadInt(); count > 0; count--) { package.ReadInt(); package.ReadSingle(); if (skills >= 2) package.ReadSingle(); }
            var custom = new List<(string, string, int, int)>();
            for (int count = package.ReadInt(); count > 0; count--)
            {
                string key = package.ReadString();
                int start = package.GetPos();
                custom.Add((key, package.ReadString(), start, package.GetPos()));
            }
            package.ReadSingle(); package.ReadSingle(); package.ReadSingle(); package.ReadByteArray();
            if (version >= (int)global::Version.PlayerData.PostLaunch)
                for (int index = 0; index < 2; index++)
                    for (int count = package.ReadInt(); count > 0; count--) { package.ReadInt(); package.ReadInt(); }
            if (package.GetPos() != data.Length) throw new InvalidDataException("its character data does not end with its last field");
            return (items, custom);
        }

        private static void PlanWorld(Pass pass)
        {
            var objects = new HashSet<ZDOID>(SavedArrays.Keys.Cast<ZDOID>().Concat(SavedTexts.Keys.Cast<ZDOID>()));
            foreach (var id in objects.OrderBy(id => id.UserID).ThenBy(id => id.ID))
            {
                var data = ZDOMan.instance.GetZDO(id);
                if (data == null || !data.Persistent) continue;
                ZDOExtraData.GetData(id, out _, out _, out _, out _, out _, out var texts, out var arrays, out _);
                string location = "world object " + id + " (" + (ZNetScene.instance.GetPrefab(data.GetPrefab())?.name ?? data.GetPrefab().ToString()) +
                    " at " + data.GetPosition().ToString("F0") + ")";
                foreach (var pair in arrays) PlanRecord(pass, data, location, pair.Key, pair.Value);
                // ItemDataManager, which AzuCraftyBoxes embeds, copies a waking drop's default custom data into the item fields
                // from before itemData. The game reads a drop only from itemData, so these copies are never loaded and stay.
                var copies = new HashSet<int>(Enumerable.Range(0, Math.Max(0, data.GetInt(ZDOVars.s_dataCount)))
                    .SelectMany(index => new[] { ("data_" + index).GetStableHashCode(), ("data__" + index).GetStableHashCode() }));
                foreach (var pair in texts)
                {
                    if (!Holds(pair.Value)) continue;
                    if (copies.Contains(pair.Key)) pass.UnusedCopies++;
                    else pass.Block(location, "a text field holds enchanted items in a format this repair does not know");
                }
            }
        }

        // Containers, dropped items, item stands, armor stand positions and the stands' extra positions, each in its own record.
        private static void PlanRecord(Pass pass, ZDO data, string location, int key, byte[] value)
        {
            if (value == null || value.Length == 0) return;
            int extra = Array.IndexOf(ArmorStandStorage.ExtraKeys, key);
            bool known = key == ZDOVars.s_items || key == ZDOVars.s_itemData || StandPositions.ContainsKey(key) || extra >= 0;
            if (!known)
            {
                if (Occurrences(value) > 0) pass.Block(location, "a field holds enchanted items in a format this repair does not know");
                return;
            }
            string store;
            List<Saved> items;
            Func<Saved, int> prefab = item => item.Prefab;
            bool active = true;
            var package = new ZPackage(value);
            try
            {
                if (key == ZDOVars.s_items) { store = "container"; items = ReadInventory(package); }
                else if (extra >= 0)
                {
                    store = extra == ArmorStandStorage.Utility ? "stand utility" : "stand trinket";
                    if (package.ReadByte() != ArmorStandStorage.Format) throw new InvalidDataException("unknown stand record format");
                    int hash = package.ReadString().GetStableHashCode();
                    int version = package.ReadByte();
                    items = new List<Saved> { ReadItem(package, version) };
                    prefab = item => hash;
                }
                else
                {
                    // A dropped item is its own prefab; a stand names its item, and a cleared stand keeps the old record unused.
                    int position = key == ZDOVars.s_itemData ? -1 : StandPositions[key];
                    int named = position >= 0 ? data.GetInt(ArmorStandStorage.ItemKey(position)) :
                        ObjectDB.instance.GetItemPrefab(data.GetPrefab()) != null ? data.GetPrefab() : data.GetInt(ZDOVars.s_item);
                    store = position >= 0 ? "armor stand position " + position : "item";
                    active = named != 0;
                    int version = package.ReadByte();
                    items = new List<Saved> { ReadItem(package, version) };
                    prefab = item => item.Prefab != 0 ? item.Prefab : named;
                }
                if (items == null || package.GetPos() != value.Length) throw new InvalidDataException("unreadable " + store);
            }
            catch (Exception error) when (error is EndOfStreamException || error is InvalidDataException)
            {
                if (Occurrences(value) > 0) pass.Block(location, error.Message);
                return;
            }
            if (Occurrences(value) != items.Count(item => item.Start >= 0))
            {
                pass.Block(location, "the " + store + " holds enchanted data outside its items");
                return;
            }
            if (!active)
            {
                pass.Inactive += items.Count(item => item.Start >= 0);
                return;
            }
            var edits = RepairItems(pass, location + " " + store, items, prefab);
            if (edits.Count > 0) pass.World.Add((data, key, Splice(value, edits)));
        }

        // An inventory payload's items from its version on, or null for the format before 108, which no current save writes.
        private static List<Saved> ReadInventory(ZPackage package)
        {
            int version = package.ReadInt();
            if (version < (int)global::Version.Item.Smaller) return null;
            var items = new List<Saved>();
            for (int count = package.ReadUShort(); count > 0; count--) items.Add(ReadItem(package, version));
            return items;
        }

        // The game's item record, read field by field as ItemDrop.ItemData.Load reads it.
        private static Saved ReadItem(ZPackage package, int version)
        {
            var item = new Saved();
            package.ReadInt(); package.ReadByte(); package.ReadByte(); package.ReadByte();
            int flags = package.ReadByte();
            if ((flags & 4) != 0) item.Quality = package.ReadUShort();
            if ((flags & 8) != 0) package.ReadUShort();
            if ((flags & 0x10) != 0) package.ReadInt();
            if ((flags & 0x20) != 0) { package.ReadLong(); package.ReadString(); }
            if ((flags & 0x40) != 0) item.Prefab = package.ReadInt();
            for (int count = (flags & 0x80) != 0 ? package.ReadNumItems() : 0; count > 0; count--)
            {
                string key = package.ReadString();
                int start = package.GetPos();
                string value = package.ReadString();
                if (key == MagicItemData.Key) { item.Magic = value; item.Start = start; item.End = package.GetPos(); }
            }
            if (version >= (int)global::Version.Item.ChunksNCheats || version == (int)global::Version.Item.AbandonedDN) package.ReadByte();
            return item;
        }

        // The replacements of the items' enchantment values that need a repair.
        private static List<(int Start, int End, byte[] Bytes)> RepairItems(Pass pass, string location, List<Saved> items, Func<Saved, int> prefab)
        {
            var edits = new List<(int, int, byte[])>();
            for (int index = 0; index < items.Count; index++)
            {
                pass.Items++;
                var item = items[index];
                if (item.Start < 0 || string.IsNullOrEmpty(item.Magic) || item.Magic == "null") continue;
                string repaired = Repair(pass, location + " item " + index, prefab(item), item.Quality, item.Magic);
                if (repaired != null) edits.Add((item.Start, item.End, Encoded(repaired)));
            }
            return edits;
        }

        // The item's repaired enchantment data, or null when it keeps its data.
        private static string Repair(Pass pass, string location, int prefabHash, int quality, string json)
        {
            pass.Enchanted++;
            var prefab = ObjectDB.instance.GetItemPrefab(prefabHash);
            if (prefab == null)
            {
                // The game cannot load an item whose prefab is gone, so it is no longer part of the campaign.
                pass.Unloadable.Add(location + ": prefab " + prefabHash);
                return null;
            }
            location += " " + prefab.name;
            JObject data;
            // Numbers keep their saved spelling, so everything the repair leaves alone is written back exactly.
            try { data = RuneStacks.ReadPayload(json, savedText: true); }
            catch (JsonException error) { pass.Block(location, "unreadable enchantment data: " + error.Message); return null; }
            var item = Item(prefab, quality, json);
            if (RuneStacks.IsRune(item) || Shards.IsShard(item)) { pass.Kept[location] = json; return null; }
            pass.Kept[location] = data["Sockets"]?.ToString(Formatting.None) ?? "";
            var magic = item.GetMagicItem();
            if (magic == null) { pass.Block(location, "EpicLoot cannot read its enchantment data"); return null; }
            if (!(data["Effects"] is JArray effects) || effects.Count != magic.Effects.Count) { pass.Block(location, "its affixes are unreadable"); return null; }
            var dead = new List<(int Slot, string Reason)>();
            for (int slot = 0; slot < magic.Effects.Count; slot++)
            {
                string reason = EnchantmentApplicability.DeadReason(item, magic, magic.Effects[slot].EffectType);
                if (reason != null) dead.Add((slot, reason));
            }
            if (dead.Count == 0) return null;
            var repairs = new List<JObject>();
            var state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(Seed(prefab.name, json));
            try
            {
                foreach (var (slot, reason) in dead)
                {
                    var old = magic.Effects[slot];
                    var beside = magic.Effects.Where((effect, index) => index != slot)
                        .Concat(magic.Sockets.Where(socket => socket?.Effect != null).Select(socket => socket.Effect)).ToList();
                    // The ordinary loot pool, which leaves out dead effects, and nothing that conflicts with a socket.
                    var pool = MagicItemEffectDefinitions.GetAvailableEffects(item, magic, slot)
                        .Where(definition => definition.Type != old.EffectType && SocketConflicts.Conflict(definition.Type, beside) == null).ToList();
                    if (pool.Count == 0) { pass.Block(location, "no effect can replace the dead " + old.EffectType + " in slot " + slot); return null; }
                    var fresh = LootRoller.RollEffect(new WeightedRandomCollection<MagicItemEffectDefinition>(pool, definition => definition.SelectionWeight).Roll(), magic.Rarity);
                    magic.Effects[slot] = fresh;
                    effects[slot] = JObject.Parse(RuneExtractionPower.Serialize(fresh));
                    repairs.Add(new JObject
                    {
                        ["location"] = location, ["slot"] = slot, ["dead"] = old.EffectType, ["dead_value"] = old.EffectValue, ["reason"] = reason,
                        ["new"] = fresh.EffectType, ["new_value"] = fresh.EffectValue,
                    });
                }
            }
            finally { UnityEngine.Random.state = state; }
            for (int slot = 0; slot < magic.Effects.Count; slot++)
                if (EnchantmentApplicability.DeadReason(item, magic, magic.Effects[slot].EffectType) is string reason)
                { pass.Block(location, "no complete valid arrangement: " + magic.Effects[slot].EffectType + " in slot " + slot + " stays dead: " + reason); return null; }
            // A repaired slot is an ordinary fresh affix: it was never augmented or tempered.
            var repaired = new HashSet<int>(dead.Select(slot => slot.Slot));
            foreach (string field in new[] { "AugmentedEffectIndices", "TemperedEffectIndices" })
                if (data[field] is JArray marks)
                    foreach (var mark in marks.Where(mark => repaired.Contains(int.Parse(mark.ToString()))).ToList()) mark.Remove();
            if (data["AugmentedEffectIndex"] is JToken single && repaired.Contains(int.Parse(single.ToString()))) data["AugmentedEffectIndex"] = -1;
            string result = data.ToString(Formatting.None);
            var reread = Item(prefab, quality, result).GetMagicItem();
            if (reread == null || !reread.Effects.Select(effect => (effect.EffectType, effect.EffectValue)).SequenceEqual(magic.Effects.Select(effect => (effect.EffectType, effect.EffectValue))) ||
                !JToken.DeepEquals(RuneStacks.ReadPayload(json, savedText: true)["Sockets"], data["Sockets"]))
            { pass.Block(location, "the repaired data does not read back as planned"); return null; }
            foreach (var repair in repairs) pass.Repairs.Add(repair);
            return result;
        }

        private static ItemDrop.ItemData Item(UnityEngine.GameObject prefab, int quality, string json)
        {
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_quality = quality;
            item.m_customData = new Dictionary<string, string> { [MagicItemData.Key] = json };
            return item;
        }

        // The same original data always gets the same replacements, wherever the item is.
        private static int Seed(string prefab, string json)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToInt32(sha.ComputeHash(Encoding.UTF8.GetBytes(prefab + "\n" + json)), 0);
        }

        private static string Digest(SortedDictionary<string, string> kept)
        {
            using var sha = SHA256.Create();
            var text = new StringBuilder();
            foreach (var pair in kept) text.Append(pair.Key).Append('\t').Append(pair.Value).Append('\n');
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        // Whether text holds enchanted item data, as plain text or as base64.
        private static bool Holds(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.Contains(MagicItemData.Key)) return true;
            if (text.Length < 16 || text.Length % 4 != 0) return false;
            try { return Occurrences(Convert.FromBase64String(text)) > 0; }
            catch (FormatException) { return false; }
        }

        private static int Occurrences(byte[] data)
        {
            int found = 0;
            for (int index = Array.IndexOf(data, KeyBytes[0]); index >= 0 && index <= data.Length - KeyBytes.Length; index = Array.IndexOf(data, KeyBytes[0], index + 1))
            {
                int matched = 1;
                while (matched < KeyBytes.Length && data[index + matched] == KeyBytes[matched]) matched++;
                if (matched == KeyBytes.Length) found++;
            }
            return found;
        }

        private static byte[] Encoded(string value)
        {
            var package = new ZPackage();
            package.Write(value);
            return package.GetArray();
        }

        private static byte[] Splice(byte[] data, List<(int Start, int End, byte[] Bytes)> edits)
        {
            using var output = new MemoryStream();
            int at = 0;
            foreach (var (start, end, bytes) in edits.OrderBy(edit => edit.Start))
            {
                output.Write(data, at, start - at);
                output.Write(bytes, 0, bytes.Length);
                at = end;
            }
            output.Write(data, at, data.Length - at);
            return output.ToArray();
        }
    }
}
