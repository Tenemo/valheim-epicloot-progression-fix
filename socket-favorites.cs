using System.Collections.Generic;
using System.Linq;
using EpicLoot;
using EpicLoot.ShardStones;
using HarmonyLib;
using Newtonsoft.Json;

namespace EpicLootProgressionFix
{
    // EpicLoot rebuilds a weapon's socketed runes from their effects each time its socket row opens, which dropped a
    // rune's own favorite. Each save of the row now records on the weapon which socket holds a favorite rune, and opening
    // the row puts that favorite back, so a rune keeps it in the socket, when taken out and when put back. Shardstone
    // favorites are type-wide and need no record.
    [HarmonyPatch]
    internal static class SocketFavorites
    {
        private const string Key = Plugin.Id + ".socket-favorites";

        internal sealed class Record
        {
            public int Socket;
            public string Effect;
            public Dictionary<string, string> Markers;
        }

        internal static bool Showing(InventoryGrid grid) => OpenSockets.Row != null && grid.GetInventory() == OpenSockets.Row;

        // EpicLoot saves the row's stones in grid order, leaving out gaps, so a record names the saved socket.
        [HarmonyPatch(typeof(ShardUI), "SaveSockets"), HarmonyPostfix]
        private static void Save()
        {
            var weapon = OpenSockets.Item;
            var row = OpenSockets.Row;
            if (weapon == null || row == null || !weapon.IsMagic(out var magic)) return;
            var occupants = row.GetAllItems().OrderBy(item => item.m_gridPos.x).ToList();
            var records = new List<Record>();
            for (int socket = 0; socket < magic.Sockets.Count && socket < occupants.Count; socket++)
            {
                string effect = magic.Sockets[socket]?.Effect?.EffectType;
                var rune = occupants[socket];
                if (effect == null || !rune.m_customData.Keys.Any(PersistentFavorites.IsMarker)) continue;
                records.Add(new Record { Socket = socket, Effect = effect,
                    Markers = rune.m_customData.Where(pair => PersistentFavorites.IsMarker(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value) });
            }
            if (records.Count > 0) weapon.m_customData[Key] = JsonConvert.SerializeObject(records);
            else weapon.m_customData.Remove(Key);
        }

        // A record whose socket now holds another effect, after the stones changed elsewhere, is ignored.
        [HarmonyPatch(typeof(ShardUI), "Open", typeof(InventoryGui), typeof(ItemDrop.ItemData)), HarmonyPostfix]
        private static void Restore(ItemDrop.ItemData item)
        {
            SocketPositions.Restore(item);
            if (item == null || OpenSockets.Item != item || OpenSockets.Row == null || !item.IsMagic(out var magic) ||
                !item.m_customData.TryGetValue(Key, out string saved)) return;
            List<Record> records;
            try { records = JsonConvert.DeserializeObject<List<Record>>(saved); }
            catch (JsonException) { return; }
            foreach (var record in records ?? new List<Record>())
            {
                var rune = OpenSockets.Row.GetItemAt(SocketPositions.Column(item, record.Socket), 0);
                if (rune == null || record.Markers == null || record.Socket >= magic.Sockets.Count ||
                    magic.Sockets[record.Socket]?.Effect?.EffectType != record.Effect) continue;
                foreach (var marker in record.Markers) rune.m_customData[marker.Key] = marker.Value;
            }
        }

        internal static void Retain(ItemDrop.ItemData item, int[] indices)
        {
            if (!item.m_customData.TryGetValue(Key, out string saved)) return;
            List<Record> records;
            try { records = JsonConvert.DeserializeObject<List<Record>>(saved); }
            catch (JsonException) { return; }
            var retained = new List<Record>();
            foreach (var record in records ?? new List<Record>())
            {
                int socket = System.Array.IndexOf(indices, record.Socket);
                if (socket < 0) continue;
                record.Socket = socket;
                retained.Add(record);
            }
            if (retained.Count > 0) item.m_customData[Key] = JsonConvert.SerializeObject(retained);
            else item.m_customData.Remove(Key);
        }

        internal static void RestoreReturned(ItemDrop.ItemData item, int socket, ItemDrop.ItemData returned)
        {
            if (!item.m_customData.TryGetValue(Key, out string saved)) return;
            List<Record> records;
            try { records = JsonConvert.DeserializeObject<List<Record>>(saved); }
            catch (JsonException) { return; }
            string effect = item.GetMagicItem().Sockets[socket]?.Effect?.EffectType;
            foreach (var record in records ?? new List<Record>())
                if (record.Socket == socket && record.Effect == effect && record.Markers != null)
                    foreach (var marker in record.Markers.Where(marker => PersistentFavorites.IsMarker(marker.Key)))
                        returned.m_customData[marker.Key] = marker.Value;
        }
    }
}
