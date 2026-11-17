using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class QuickStackSorting
    {
        private const string RequestRpc = "ProgressionFixSortChest";
        private const string ResultRpc = "ProgressionFixSortChestResult";
        private static FieldInfo criterion, mergeStacks;
        private static Comparison<ItemDrop.ItemData> compare;
        private static Action<List<ItemDrop.ItemData>, Inventory> merge;
        private static readonly Func<Container, long, bool> CanAccess = AccessTools.MethodDelegate<Func<Container, long, bool>>(
            AccessTools.Method(typeof(Container), "CheckAccess"));
        private static readonly Action<Inventory, bool, bool> Changed = AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(
            AccessTools.Method(typeof(Inventory), "Changed", new[] { typeof(bool), typeof(bool) }));

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.QuickStack, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var sorting = assembly.GetType("QuickStackStore.SortModule", true);
            var config = assembly.GetType("QuickStackStore.QSSConfig+SortConfig", true);
            criterion = AccessTools.Field(config, "SortCriteria");
            mergeStacks = AccessTools.Field(config, "SortMergesStacks");
            compare = AccessTools.MethodDelegate<Comparison<ItemDrop.ItemData>>(AccessTools.Method(sorting, "SortCompare"));
            merge = AccessTools.MethodDelegate<Action<List<ItemDrop.ItemData>, Inventory>>(AccessTools.Method(sorting, "MergeStacks"));
            harmony.Patch(AccessTools.Method(sorting, "MergeStacks"), prefix: new HarmonyMethod(typeof(QuickStackSorting), nameof(MergeStacks)));
            if (criterion == null || mergeStacks == null) throw new MissingMemberException("Quick Stack's sort settings changed.");
            harmony.Patch(AccessTools.Method(sorting, "SortByGetter"), postfix: new HarmonyMethod(typeof(QuickStackSorting), nameof(StackWeight)));
            harmony.Patch(AccessTools.Method(sorting, "GetAllowedSlots"), postfix: new HarmonyMethod(typeof(QuickStackSorting), nameof(TopFirst)));
            harmony.Patch(AccessTools.Method(sorting, "SortContainer"), prefix: new HarmonyMethod(typeof(QuickStackSorting), nameof(SortContainer)));
            log.LogInfo("Quick Stack sorts by total stack weight and preserves favorite items in chests, including remote-owned chests.");
        }

        private static void StackWeight(ItemDrop.ItemData item, ref IComparable __result)
        {
            if (((ConfigEntryBase)criterion.GetValue(null)).BoxedValue.ToString() == "Weight") __result = item.GetWeight();
        }

        // Sorting leaves the empty cell of a thrown recalling weapon free; it still has a cell for every item it moves.
        private static void TopFirst(Inventory inventory, List<Vector2i> __result)
        {
            __result.RemoveAll(cell => RecallRecovery.Reserves(inventory, cell));
            __result.Sort((left, right) => left.y != right.y ? left.y.CompareTo(right.y) : left.x.CompareTo(right.x));
        }

        private static bool SortContainer(Container container)
        {
            var player = Player.m_localPlayer;
            if (player == null) return true;
            var inventory = container.GetInventory();
            var protectedSlots = new HashSet<Vector2i>(inventory.GetAllItems().Where(PersistentFavorites.IsFavoriteItem)
                .Select(item => item.m_gridPos));
            if (container.IsOwner()) SortChest(inventory, protectedSlots);
            else
            {
                // Favorites belong to the requester. The owner receives their protected positions with the exact
                // inventory snapshot, so a concurrent transfer cannot make those positions protect different items.
                var request = new ZPackage();
                request.Write(Snapshot(inventory));
                request.Write(protectedSlots.Count);
                foreach (var position in protectedSlots) { request.Write(position.x); request.Write(position.y); }
                GameFields.ContainerView(container).InvokeRPC(RequestRpc, request);
            }
            return false;
        }

        private static byte[] Snapshot(Inventory inventory)
        {
            var package = new ZPackage();
            inventory.Save(package);
            return package.GetArray();
        }

        private static void SortChest(Inventory inventory, HashSet<Vector2i> protectedSlots)
        {
            var items = inventory.GetAllItems().Where(item => !protectedSlots.Contains(item.m_gridPos)).ToList();
            if ((bool)((ConfigEntryBase)mergeStacks.GetValue(null)).BoxedValue) merge(items, inventory);
            items.Sort(compare);
            int index = 0;
            for (int row = 0; row < inventory.GetHeight(); row++)
                for (int column = 0; column < inventory.GetWidth() && index < items.Count; column++)
                {
                    var position = new Vector2i(column, row);
                    if (!protectedSlots.Contains(position)) items[index++].m_gridPos = position;
                }
            Changed(inventory, false, false);
        }

        private const string EpicLootData = "randyknapp.mods.epicloot#";

        // Both Sort buttons merge here. Quick Stack pools stacks by name and quality only when they carry no custom data, so
        // EpicLoot's built-in Wishbone effect kept ordinary Wishbones apart. Merge each stack into earlier stacks that accept
        // it as a drag would instead; EpicLoot's check also covers favorite markers, etched runes and modified Wishbones.
        // Other mods' data still keeps a stack out. The list already leaves out favorites, protected slots and equipment slots.
        private static bool MergeStacks(List<ItemDrop.ItemData> toMerge, Inventory inventory)
        {
            bool Mergeable(ItemDrop.ItemData item) => item.m_customData.Keys.All(key =>
                key.StartsWith(EpicLootData, StringComparison.Ordinal) || PersistentFavorites.IsMarker(key));
            for (int index = 0; index < toMerge.Count; index++)
            {
                var source = toMerge[index];
                if (!Mergeable(source)) continue;
                for (int earlier = 0; earlier < index && source.m_stack > 0; earlier++)
                {
                    var target = toMerge[earlier];
                    int space = target.m_shared.m_maxStackSize - target.m_stack;
                    if (space <= 0 || target.m_quality != source.m_quality || !target.IsSameType(source) || !Mergeable(target)) continue;
                    if (!RuneStacks.StacksWith(target, source)) continue;
                    int quantity = Math.Min(space, source.m_stack);
                    target.m_stack += quantity;
                    source.m_stack -= quantity;
                    PersistentFavorites.KeepMarkers(target, source);
                }
                if (source.m_stack > 0) continue;
                inventory.RemoveItem(source);
                toMerge.RemoveAt(index--);
            }
            return false;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Container), "Awake")]
        private static void Register(Container __instance, ZNetView ___m_nview)
        {
            if (compare == null || ___m_nview == null || !___m_nview.IsValid()) return;
            ___m_nview.Register<ZPackage>(RequestRpc, (sender, request) => Receive(__instance, ___m_nview, sender, request));
            ___m_nview.Register<bool>(ResultRpc, (sender, sorted) =>
            {
                if (!sorted && sender == ___m_nview.GetZDO().GetOwner())
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "The chest changed before sorting. Click Sort again.");
            });
        }

        private static void Receive(Container container, ZNetView view, long sender, ZPackage request)
        {
            if (!view.IsOwner() || !Plugin.Ready) return;
            long playerId;
            var player = Player.GetAllPlayers().FirstOrDefault(candidate => candidate.GetOwner() == sender);
            if (player != null) playerId = player.GetPlayerID();
            else
            {
                // Dedicated servers need not instantiate Player objects. Resolve the authenticated peer's character
                // from its ZDO instead, keeping the character identity out of the request payload.
                var peer = ZNet.instance.GetPeers().FirstOrDefault(candidate => candidate.m_uid == sender);
                if (peer == null || !peer.IsReady()) return;
                var character = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (character == null || character.GetPrefab() != "Player".GetStableHashCode() || character.GetOwner() != sender) return;
                playerId = character.GetLong(ZDOVars.s_playerID);
            }
            if (playerId == 0L || !CanAccess(container, playerId)) return;
            var inventory = container.GetInventory();
            var current = Snapshot(inventory);
            int requestSize = request.GetArray().Length;
            if (requestSize < current.Length + 8 || requestSize > current.Length + 8 + inventory.NrOfItems() * 8) return;
            if (request.ReadInt() != current.Length) return;
            request.SetPos(0);
            var snapshot = request.ReadByteArray();
            if (!snapshot.SequenceEqual(current)) { view.InvokeRPC(sender, ResultRpc, false); return; }
            int count = request.ReadInt();
            if (count < 0 || count > inventory.NrOfItems() || requestSize != current.Length + 8 + count * 8) return;
            var protectedSlots = new HashSet<Vector2i>();
            for (int index = 0; index < count; index++)
            {
                int column = request.ReadInt(), row = request.ReadInt();
                if (inventory.GetItemAt(column, row) == null || !protectedSlots.Add(new Vector2i(column, row))) return;
            }
            SortChest(inventory, protectedSlots);
            view.InvokeRPC(sender, ResultRpc, true);
        }
    }
}
