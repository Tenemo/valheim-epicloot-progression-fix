using System;
using System.Collections.Generic;
using System.Linq;
using EpicLoot;
using EpicLoot.ShardStones;
using HarmonyLib;
using Newtonsoft.Json;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class SocketPositions
    {
        private const string Key = Plugin.Id + ".socket-positions";

        private static int[] Read(ItemDrop.ItemData item, int count)
        {
            if (item.m_customData.TryGetValue(Key, out string saved))
            {
                try
                {
                    var positions = JsonConvert.DeserializeObject<int[]>(saved);
                    if (positions != null && positions.Length == count && positions.Distinct().Count() == count &&
                        positions.All(column => column >= 0 && column < item.GetMagicItem().SocketCount)) return positions;
                }
                catch (JsonException) { }
            }
            // Older items have no position record. Their compact native layout is the only recoverable one.
            return Enumerable.Range(0, count).ToArray();
        }

        private static void Store(ItemDrop.ItemData item, int[] positions)
        {
            if (positions.SequenceEqual(Enumerable.Range(0, positions.Length))) item.m_customData.Remove(Key);
            else item.m_customData[Key] = JsonConvert.SerializeObject(positions);
        }

        internal static int Column(ItemDrop.ItemData item, int socket)
        {
            var positions = Read(item, item.GetMagicItem().Sockets.Count);
            return socket >= 0 && socket < positions.Length ? positions[socket] : socket;
        }

        internal static int Index(ItemDrop.ItemData item, int column) => Array.IndexOf(Read(item, item.GetMagicItem().Sockets.Count), column);

        [HarmonyPatch(typeof(ShardUI), "SaveSockets"), HarmonyPostfix]
        private static void Save()
        {
            var item = OpenSockets.Item;
            var row = OpenSockets.Row;
            if (item == null || row == null || !item.IsMagic(out var magic)) return;
            var positions = row.GetAllItems().OrderBy(occupant => occupant.m_gridPos.x).Select(occupant => occupant.m_gridPos.x).ToArray();
            if (positions.Length == magic.Sockets.Count) Store(item, positions);
        }

        internal static void Restore(ItemDrop.ItemData item)
        {
            if (item == null || OpenSockets.Item != item || OpenSockets.Row == null || !item.IsMagic(out var magic)) return;
            var positions = Read(item, magic.Sockets.Count);
            var occupants = OpenSockets.Row.GetAllItems().OrderBy(occupant => occupant.m_gridPos.x).ToArray();
            // Move all cells directly; publishing intermediate inventory changes would compact the saved socket list.
            for (int index = 0; index < Math.Min(occupants.Length, positions.Length); index++)
                occupants[index].m_gridPos = new Vector2i(positions[index], 0);
        }

        internal static void Retain(ItemDrop.ItemData item, IReadOnlyList<SocketedEffect> before, IList<SocketedEffect> retained)
        {
            var indices = Enumerable.Range(0, before.Count).Where(index => retained.Any(socket => ReferenceEquals(socket, before[index]))).ToArray();
            Remap(item, Read(item, before.Count), indices);
        }

        private static void Remap(ItemDrop.ItemData item, int[] positions, int[] indices)
        {
            Store(item, indices.Select(index => positions[index]).ToArray());
            SocketFavorites.Retain(item, indices);
        }

        [HarmonyPatch(typeof(ShardSocketManager), nameof(ShardSocketManager.AddShard))]
        private static class Added
        {
            private static void Prefix(ItemDrop.ItemData __0, out int[] __state)
            {
                if (__0 != null) RerollShardstones.CloseOpenSockets(__0);
                __state = __0 != null && __0.IsMagic(out var magic) ? Read(__0, magic.Sockets.Count) : null;
            }

            private static void Postfix(ItemDrop.ItemData __0, bool __result, int[] __state)
            {
                if (!__result || __state == null) return;
                var magic = __0.GetMagicItem();
                int free = Enumerable.Range(0, magic.SocketCount).First(column => !__state.Contains(column));
                var positions = __state.Concat(new[] { free }).ToArray();
                var order = Enumerable.Range(0, positions.Length).OrderBy(index => positions[index]).ToArray();
                Remap(__0, positions, order);
                if (order.SequenceEqual(Enumerable.Range(0, positions.Length))) return;
                // Match insertion through the row: filling an earlier gap also sets native stacking order now, rather
                // than changing strengths later when the overlay first saves its visual order.
                magic.Sockets = order.Select(index => magic.Sockets[index]).ToList();
                ShardSocketManager.RecomputeSocketValues(__0, magic);
                __0.SaveMagicItem(magic);
            }
        }

        [HarmonyPatch(typeof(ShardSocketManager), nameof(ShardSocketManager.RemoveShard))]
        private static class Removed
        {
            private static void Prefix(ItemDrop.ItemData __0, out int[] __state)
            {
                if (__0 != null) RerollShardstones.CloseOpenSockets(__0);
                __state = __0 != null && __0.IsMagic(out var magic) ? Read(__0, magic.Sockets.Count) : null;
            }

            private static void Postfix(ItemDrop.ItemData __0, int __1, int[] __state)
            {
                if (__state == null || __0.GetMagicItem().Sockets.Count == __state.Length) return;
                Remap(__0, __state, Enumerable.Range(0, __state.Length).Where(index => index != __1).ToArray());
            }
        }
    }
}
