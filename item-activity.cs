using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using EpicLoot;
using EpicLoot.Adventure;
using EpicLoot.Adventure.Feature;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A private journal of how items reach players. Each operation that can add items to the local player's inventory,
    // a pickup, chest transfer, craft, upgrade, refinement, purchase, bounty reward or enchanting change, compares the
    // inventory's totals before and after and records only what actually changed, so failed pickups, cancelled crafts and
    // splitting or moving stacks record nothing. Additions no operation explains are recorded as unknown.
    //
    // Creature drops, harvests, resources and production outputs carry their origin in the dropped object's saved data, so
    // a later pickup by anyone names where it first came from. Equipment carries a tracking id in its own data, so its
    // pickup, storage, refinement and enchanting form one history; equipment a character already carried begins its
    // history at the character's first recorded session. Stackable resources carry nothing, so stacking is unchanged.
    [HarmonyPatch]
    internal static class ItemActivity
    {
        internal const string TrackingKey = Plugin.Id + ".item-activity";
        internal static readonly int OriginKey = (Plugin.Id + ".item-activity.origin").GetStableHashCode();
        private static readonly AccessTools.FieldRef<InventoryGui, Container> OpenContainer = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
        private static readonly AccessTools.FieldRef<InventoryGui, Recipe> CraftRecipe = AccessTools.FieldRefAccess<InventoryGui, Recipe>("m_craftRecipe");
        private static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> CraftUpgrade = AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_craftUpgradeItem");
        private static readonly AccessTools.FieldRef<InventoryGui, bool> MultiCrafting = AccessTools.FieldRefAccess<InventoryGui, bool>("m_multiCrafting");
        private static readonly AccessTools.FieldRef<StoreGui, Trader> StoreTrader = AccessTools.FieldRefAccess<StoreGui, Trader>("m_trader");
        private static readonly AccessTools.FieldRef<ItemDrop, ZNetView> DropView = AccessTools.FieldRefAccess<ItemDrop, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Player, bool> Loading = AccessTools.FieldRefAccess<Player, bool>("m_isLoading");
        private static readonly ConditionalWeakTable<Inventory, Container> Containers = new ConditionalWeakTable<Inventory, Container>();
        private static Component origin;
        private static string carriedOrigin;
        private static Context context;
        private static int implicitDepth;
        private static Dictionary<Key, Entry> implicitBefore;

        private struct Key : IEquatable<Key>
        {
            internal string Item; internal int Quality; internal object Identity;
            public bool Equals(Key other) => Item == other.Item && Quality == other.Quality && Equals(Identity, other.Identity);
            public override bool Equals(object other) => other is Key key && Equals(key);
            public override int GetHashCode() => ((Item?.GetHashCode() ?? 0) * 31 + Quality) * 31 + (Identity?.GetHashCode() ?? 0);
        }
        private sealed class Entry { internal int Count; internal ItemDrop.ItemData Item; }
        private struct Change { internal Key Key; internal ItemDrop.ItemData Item; internal int Delta; }

        private sealed class Context
        {
            internal string Action, Source, Origin, Detail;
            internal Vector3? At;
            internal bool Silent;
            internal Dictionary<Key, Entry> Before;
            // Only items with this shared name can change, so the totals leave out the rest.
            internal string Only;
            internal Context Previous;
            internal Action<Context, List<Change>> Finish;
            internal ItemDrop.ItemData Upgrade;
            internal string UpgradeTracking, Station, Recipe, Resource;
            internal int UpgradeQuality, Expected;
            internal bool Upgrader;
        }

        private static string Name(ItemDrop.ItemData item) => item.m_dropPrefab ? item.m_dropPrefab.name : item.m_shared.m_name;
        internal static bool Unique(ItemDrop.ItemData item) => item.m_shared.m_maxStackSize <= 1 && item.IsEquipable();
        internal static string Position(Vector3 at) => $"{at.x:0},{at.z:0}";
        private static Player Local(Inventory inventory)
        {
            var player = Player.m_localPlayer;
            return player != null && ItemActivityChannel.Active && player.GetInventory() == inventory && !Loading(player) ? player : null;
        }

        private static Dictionary<Key, Entry> Snapshot(Inventory inventory, string only = null)
        {
            var totals = new Dictionary<Key, Entry>();
            foreach (var item in inventory.GetAllItems())
            {
                if (only != null && item.m_shared.m_name != only) continue;
                object identity = null;
                if (Unique(item)) identity = item.m_customData.TryGetValue(TrackingKey, out string tracking) ? (object)tracking : item;
                var key = new Key { Item = Name(item), Quality = item.m_quality, Identity = identity };
                if (totals.TryGetValue(key, out var entry)) entry.Count += item.m_stack;
                else totals.Add(key, new Entry { Count = item.m_stack, Item = item });
            }
            return totals;
        }

        private static List<Change> Diff(Dictionary<Key, Entry> before, Dictionary<Key, Entry> after)
        {
            var changes = new List<Change>();
            foreach (var pair in after)
            {
                int delta = pair.Value.Count - (before.TryGetValue(pair.Key, out var old) ? old.Count : 0);
                if (delta != 0) changes.Add(new Change { Key = pair.Key, Item = pair.Value.Item, Delta = delta });
            }
            foreach (var pair in before)
                if (!after.ContainsKey(pair.Key) && pair.Value.Count != 0) changes.Add(new Change { Key = pair.Key, Item = pair.Value.Item, Delta = -pair.Value.Count });
            return changes;
        }

        // The equipment's tracking id, given now when it has none.
        private static string Track(ItemDrop.ItemData item, out bool begins)
        {
            begins = false;
            if (!Unique(item)) return null;
            if (item.m_customData.TryGetValue(TrackingKey, out string tracking)) return tracking;
            item.m_customData[TrackingKey] = tracking = Guid.NewGuid().ToString("N");
            begins = true;
            return tracking;
        }

        // New items, as crafted ones, start their history here; other untracked equipment begins it here.
        private static void Emit(string action, Change change, string source, string origin, string detail, Vector3? at, bool fresh = false)
        {
            string tracking;
            bool begins = false;
            if (change.Delta < 0) tracking = change.Key.Identity as string;
            else { tracking = Track(change.Item, out begins); begins &= !fresh; }
            ItemActivityChannel.Record(action, change.Key.Item, Math.Abs(change.Delta), change.Key.Quality, tracking, begins, source, origin, detail, at);
        }

        private static Context Enter(string action, string source = null, string origin = null, Vector3? at = null, string only = null)
        {
            var player = Player.m_localPlayer;
            if (Local(player?.GetInventory()) == null) return null;
            var entered = new Context { Action = action, Source = source, Origin = origin, At = at, Previous = context, Only = only, Before = Snapshot(player.GetInventory(), only) };
            context = entered;
            return entered;
        }

        private static void Exit(Context leaving)
        {
            if (leaving == null) return;
            context = leaving.Previous;
            try
            {
                var player = Player.m_localPlayer;
                if (player == null) return;
                var changes = Diff(leaving.Before, Snapshot(player.GetInventory(), leaving.Only));
                if (!leaving.Silent)
                {
                    if (leaving.Finish != null) leaving.Finish(leaving, changes);
                    else
                        foreach (var change in changes)
                            if (change.Delta > 0 || leaving.Action == "deposit")
                                Emit(change.Delta > 0 && leaving.Action == "deposit" ? "withdraw" : leaving.Action, change, leaving.Source, leaving.Origin, leaving.Detail, leaving.At);
                }
                // An enclosing operation, or an unexplained inventory change around this one, leaves out what this one
                // already recorded, counted after any equipment it recorded received its tracking id.
                var outer = leaving.Previous?.Before ?? (implicitDepth > 0 ? implicitBefore : null);
                if (outer != null)
                    foreach (var change in Diff(leaving.Before, Snapshot(player.GetInventory(), leaving.Only)))
                    {
                        if (outer.TryGetValue(change.Key, out var entry)) entry.Count += change.Delta;
                        else outer.Add(change.Key, new Entry { Count = change.Delta, Item = change.Item });
                    }
            }
            catch (Exception error) { ItemActivityChannel.Error(error); }
        }

        internal static string Describe(Container container)
        {
            if (!container) return "closed chest";
            var root = container.m_rootObjectOverride ? container.m_rootObjectOverride.gameObject : container.gameObject;
            var tombstone = container.GetComponent<TombStone>();
            var piece = root.GetComponent<Piece>() ?? container.GetComponent<Piece>();
            string kind = tombstone ? "grave of " + tombstone.GetOwnerName() : piece != null && piece.IsPlacedByPlayer() ? "storage" : "treasure";
            var zdo = root.GetComponent<ZNetView>()?.GetZDO();
            return $"{kind} {Utils.GetPrefabName(root)} {(zdo != null ? zdo.m_uid.ToString() : "unsaved")} at {Position(root.transform.position)}";
        }

        // Equipment the character carries without a tracking id begins its history in its first recorded session.
        internal static void BeginSession(Player player)
        {
            foreach (var item in player.GetInventory().GetAllItems().Where(item => Unique(item) && !item.m_customData.ContainsKey(TrackingKey)))
            {
                string tracking = Track(item, out _);
                ItemActivityChannel.Record("begin", Name(item), item.m_stack, item.m_quality, tracking, true, "carried", null, null);
            }
        }

        [HarmonyPatch(typeof(Container), "Awake"), HarmonyPostfix]
        private static void ContainerCreated(Container __instance)
        {
            var inventory = __instance.GetInventory();
            if (inventory != null) Containers.Remove(inventory);
            if (inventory != null) Containers.Add(inventory, __instance);
        }

        // Inventory changes no operation explains: with a chest open they are withdrawals and deposits, otherwise
        // additions are unknown and removals, such as eating, building or dropping, are not acquisitions.
        [HarmonyPatch]
        private static class InventoryChange
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(Inventory).GetMethods(AccessTools.all).Where(method =>
                method.DeclaringType == typeof(Inventory) && !method.IsAbstract &&
                (method.Name == "AddItem" || method.Name == "RemoveItem" || method.Name == "RemoveOneItem" || method.Name == "RemoveAll"));
            private static void Prefix(Inventory __instance, out bool __state) => Begin(Local(__instance), out __state);
            private static Exception Finalizer(bool __state, Exception __exception) => End(__state, __exception);

            internal static void Begin(Player player, out bool state)
            {
                state = false;
                if (context != null || !ItemActivityChannel.Enabled) return;
                if (implicitDepth > 0) { implicitDepth++; state = true; return; }
                if (player == null) return;
                implicitBefore = Snapshot(player.GetInventory());
                implicitDepth = 1; state = true;
            }
            internal static Exception End(bool state, Exception exception)
            {
                if (!state || --implicitDepth > 0) return exception;
                try
                {
                    var player = Player.m_localPlayer;
                    if (player == null || implicitBefore == null) return exception;
                    var changes = Diff(implicitBefore, Snapshot(player.GetInventory()));
                    if (changes.Count == 0) return exception;
                    var gui = InventoryGui.instance;
                    var container = gui && gui.IsContainerOpen() ? OpenContainer(gui) : null;
                    string source = container ? Describe(container) : null;
                    foreach (var change in changes)
                    {
                        if (container) Emit(change.Delta > 0 ? container.GetComponent<TombStone>() ? "grave" : "withdraw" : "deposit", change, source, null, null, container.transform.position);
                        else if (change.Delta > 0) Emit("unknown", change, null, null, null, null);
                    }
                }
                catch (Exception error) { ItemActivityChannel.Error(error); }
                finally { implicitBefore = null; }
                return exception;
            }
        }

        // A chest's own moves take from the player's stack before removing it, so they are watched from the chest's side too.
        [HarmonyPatch]
        private static class InventoryTransfer
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(Inventory).GetMethods(AccessTools.all).Where(method =>
                method.DeclaringType == typeof(Inventory) && (method.Name == "MoveItemToThis" || method.Name == "MoveAll" || method.Name == "StackAll") &&
                method.GetParameters().FirstOrDefault()?.ParameterType == typeof(Inventory));
            private static void Prefix(Inventory __instance, Inventory fromInventory, out bool __state) =>
                InventoryChange.Begin(Local(__instance) ?? Local(fromInventory), out __state);
            private static Exception Finalizer(bool __state, Exception __exception) => InventoryChange.End(__state, __exception);
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.Pickup))]
        private static class Pickups
        {
            private static void Prefix(Humanoid __instance, GameObject go, out Context __state)
            {
                __state = null;
                if (__instance != Player.m_localPlayer || !go) return;
                var drop = go.GetComponent<ItemDrop>();
                var zdo = drop ? DropView(drop)?.GetZDO() : null;
                if (zdo == null) return;
                // A pickup changes only the picked kind of item, which keeps mass pickups cheap.
                __state = Enter("pickup", "ground", zdo.GetString(OriginKey, "unknown"), go.transform.position, drop.m_itemData.m_shared.m_name);
            }
            private static Exception Finalizer(Context __state, Exception __exception) { Exit(__state); return __exception; }
        }

        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        private static class Crafting
        {
            private static void Prefix(InventoryGui __instance, Player player, out Context __state)
            {
                __state = null;
                var recipe = CraftRecipe(__instance);
                if (recipe == null || player != Player.m_localPlayer) return;
                var station = player.GetCurrentCraftingStation();
                var upgrade = CraftUpgrade(__instance);
                bool upgrader = station && station.m_upgrader;
                __state = Enter(upgrade == null ? "craft" : upgrader ? "refine" : "upgrade", station ? Utils.GetPrefabName(station.gameObject) : "hands");
                if (__state == null) return;
                __state.Recipe = recipe.name; __state.Upgrader = upgrader; __state.Station = __state.Source;
                __state.Expected = recipe.m_amount * (MultiCrafting(__instance) ? __instance.m_multiCraftAmount : 1);
                __state.Resource = recipe.m_resources.FirstOrDefault(requirement => requirement.m_upgraderResource)?.m_resItem?.gameObject.name;
                if (upgrade != null)
                {
                    __state.Upgrade = upgrade; __state.UpgradeQuality = upgrade.m_quality; __state.UpgradeTracking = Track(upgrade, out bool begins);
                    if (begins) ItemActivityChannel.Record("begin", Name(upgrade), 1, upgrade.m_quality, __state.UpgradeTracking, true, "carried", null, null);
                    // The tracking id is part of the item's identity in the totals taken before it was given.
                    __state.Before = Snapshot(player.GetInventory());
                }
                __state.Finish = FinishCrafting;
            }
            private static Exception Finalizer(Context __state, Exception __exception) { Exit(__state); return __exception; }
        }

        private static void FinishCrafting(Context craft, List<Change> changes)
        {
            if (craft.Upgrade == null)
            {
                foreach (var change in changes.Where(change => change.Delta > 0))
                    Emit("craft", change, craft.Station, null, $"recipe {craft.Recipe}; {craft.Expected} expected; bonus {Math.Max(0, change.Delta - craft.Expected)}", null, true);
                return;
            }
            string item = Name(craft.Upgrade);
            var result = changes.Where(change => change.Delta > 0 && change.Key.Item == item).Select(change => (Change?)change).FirstOrDefault();
            if (result.HasValue && !result.Value.Item.m_customData.ContainsKey(TrackingKey))
                result.Value.Item.m_customData[TrackingKey] = craft.UpgradeTracking;
            int? quality = result?.Key.Quality;
            string outcome = !craft.Upgrader ? "upgraded" : quality == null ? "destroyed" : quality > craft.UpgradeQuality ? "succeeded" : "reduced";
            string consumed = string.Join(", ", changes.Where(change => change.Delta < 0 && change.Key.Item == craft.Resource).Select(change => $"{change.Key.Item} x{-change.Delta}"));
            string returned = string.Join(", ", changes.Where(change => change.Delta > 0 && change.Key.Item != item).Select(change => $"{change.Key.Item} x{change.Delta}"));
            if (craft.Upgrader && consumed.Length == 0) consumed = "none recorded";
            string detail = $"Q{craft.UpgradeQuality} to {(quality.HasValue ? "Q" + quality : "destroyed")}; {outcome}" +
                (craft.Upgrader ? $"; consumed {consumed}" : "") + (returned.Length > 0 ? $"; returned {returned}" : "");
            ItemActivityChannel.Record(craft.Action, item, 1, quality ?? 0, craft.UpgradeTracking, false, craft.Station, null, detail);
        }

        [HarmonyPatch(typeof(StoreGui), "BuySelectedItem")]
        private static class Purchases
        {
            private static void Prefix(StoreGui __instance, out Context __state)
            {
                var trader = StoreTrader(__instance);
                __state = Enter("purchase", "trader " + (trader ? Utils.GetPrefabName(trader.gameObject) : "unknown"));
            }
            private static Exception Finalizer(Context __state, Exception __exception) { Exit(__state); return __exception; }
        }

        [HarmonyPatch(typeof(MerchantPanel), nameof(MerchantPanel.BuyItem))]
        private static class MerchantPurchases
        {
            private static void Prefix(out Context __state) => __state = Enter("purchase", "EpicLoot merchant");
            private static Exception Finalizer(Context __state, Exception __exception) { Exit(__state); return __exception; }
        }

        [HarmonyPatch(typeof(BountiesAdventureFeature), nameof(BountiesAdventureFeature.ClaimBountyReward))]
        private static class Bounties
        {
            private static void Prefix(BountyInfo bountyInfo, out Context __state)
            {
                __state = bountyInfo?.State == BountyState.Complete ? Enter("bounty", "bounty " + bountyInfo.TargetName) : null;
                if (__state != null) __state.Detail = "bounty " + bountyInfo.ID;
            }
            private static Exception Finalizer(Context __state, Exception __exception) { Exit(__state); return __exception; }
        }

        // MultiUserChest settles chest transfers in responses. A withdrawal or swap counts only when the chest granted
        // it; a refused transfer returns the player's own item, and the request itself moves nothing yet.
        [HarmonyPatch]
        private static class ChestResponses
        {
            private static MethodInfo getRequest;
            private static bool Prepare()
            {
                if (!PluginIds.TryGet(PluginIds.MultiUserChest, out var plugin)) return false;
                var assembly = plugin.Instance.GetType().Assembly;
                getRequest = AccessTools.Method(assembly.GetType("MultiUserChest.PackageHandler", true), "GetPackage", new[] { typeof(int) },
                    new[] { assembly.GetType("MultiUserChest.IRequest", true) }) ?? throw new MissingMethodException("MultiUserChest.PackageHandler", "GetPackage");
                return true;
            }
            private static IEnumerable<MethodBase> TargetMethods()
            {
                PluginIds.TryGet(PluginIds.MultiUserChest, out var plugin);
                var assembly = plugin.Instance.GetType().Assembly;
                var handler = assembly.GetType("MultiUserChest.InventoryHandler", true);
                foreach (string name in new[] { "RPC_RequestItemRemoveResponse", "RPC_RequestItemAddResponse" })
                    yield return handler.GetMethods(AccessTools.all).Single(method => method.Name == name && method.GetParameters().Length == 2 &&
                        method.GetParameters()[0].ParameterType == typeof(Inventory));
            }
            // Never throws into MultiUserChest's handler: a failure here loses only the journal entry.
            private static void Prefix(MethodBase __originalMethod, object __1, out Context __state)
            {
                __state = null;
                try
                {
                    if (!ItemActivityChannel.Active) return;
                    var response = Traverse.Create(__1);
                    bool success = response.Property<bool>("Success").Value, adding = __originalMethod.Name.Contains("Add");
                    var request = getRequest.Invoke(null, new object[] { response.Property<int>("SourceID").Value });
                    var chestInventory = request == null ? null : Traverse.Create(request).Property<Inventory>(adding ? "TargetInventory" : "SourceInventory").Value;
                    Container container = null;
                    if (chestInventory != null) Containers.TryGetValue(chestInventory, out container);
                    // A granted deposit left the player when it was requested; the response says how much of it the chest took,
                    // since the chest may change the request's copy of the dragged item. A swapped chest item arrives here.
                    var dragged = success && adding && request != null ? Traverse.Create(request).Field<ItemDrop.ItemData>("dragItem").Value : null;
                    int deposited = dragged != null ? response.Property<int>("Amount").Value : 0;
                    var key = dragged == null ? default : new Key { Item = Name(dragged), Quality = dragged.m_quality,
                        Identity = Unique(dragged) && dragged.m_customData.TryGetValue(TrackingKey, out string tracking) ? tracking : null };
                    __state = Enter(success ? container && container.GetComponent<TombStone>() ? "grave" : "withdraw" : "return", Describe(container), null,
                        container ? container.transform.position : (Vector3?)null);
                    if (__state == null) return;
                    __state.Silent = !success;
                    if (dragged != null) __state.Finish = (deposit, changes) =>
                    {
                        if (deposited > 0) Emit("deposit", new Change { Key = key, Item = dragged, Delta = -deposited }, deposit.Source, null, null, deposit.At);
                        foreach (var change in changes.Where(change => change.Delta > 0 && !(change.Key.Item == key.Item && change.Key.Quality == key.Quality)))
                            Emit(deposit.Action, change, deposit.Source, null, null, deposit.At);
                    };
                }
                catch (Exception error) { ItemActivityChannel.Error(error); }
            }
            private static Exception Finalizer(Context __state, Exception __exception) { Exit(__state); return __exception; }
        }

        // The game's Take all, also used when Use on one's own grave takes everything without opening it.
        [HarmonyPatch(typeof(Container), "RPC_TakeAllResponse")]
        private static class TakeAll
        {
            private static void Prefix(Container __instance, bool granted, out Context __state) =>
                __state = granted ? Enter(__instance.GetComponent<TombStone>() ? "grave" : "withdraw", Describe(__instance), null, __instance.transform.position) : null;
            private static Exception Finalizer(Context __state, Exception __exception) { Exit(__state); return __exception; }
        }

        [HarmonyPatch]
        private static class ChestRequests
        {
            private static bool Prepare() => PluginIds.TryGet(PluginIds.MultiUserChest, out _);
            private static IEnumerable<MethodBase> TargetMethods()
            {
                PluginIds.TryGet(PluginIds.MultiUserChest, out var plugin);
                var handler = plugin.Instance.GetType().Assembly.GetType("MultiUserChest.ContainerHandler", true);
                yield return AccessTools.Method(handler, "AddItemToChest");
                yield return AccessTools.Method(handler, "RemoveItemFromChest");
            }
            private static void Prefix(out Context __state)
            {
                __state = Enter("request");
                if (__state != null) __state.Silent = true;
            }
            private static Exception Finalizer(Context __state, Exception __exception) { Exit(__state); return __exception; }
        }

        [HarmonyPatch]
        private static class Restocking
        {
            private static bool Prepare() => PluginIds.TryGet(PluginIds.QuickStack, out _);
            private static IEnumerable<MethodBase> TargetMethods()
            {
                PluginIds.TryGet(PluginIds.QuickStack, out var plugin);
                var assembly = plugin.Instance.GetType().Assembly;
                yield return AccessTools.Method(assembly.GetType("QuickStackStore.RestockModule", true), "DoRestock");
                yield return AccessTools.Method(assembly.GetType("QuickStackStore.QuickStackModule", true), "DoQuickStack");
            }
            private static void Prefix(MethodBase __originalMethod, out Context __state) =>
                __state = __originalMethod.Name == "DoRestock" ? Enter("withdraw", "restock from nearby storage") : Enter("deposit", "quick stack to nearby storage");
            private static Exception Finalizer(Context __state, Exception __exception) { Exit(__state); return __exception; }
        }

        // EpicLoot saves every change to an item's magic through SetMagicItem; one that changes the saved value of an item
        // the player carries is an enchanting change, recorded with the item's new rarity, effects and sockets.
        [HarmonyPatch(typeof(MagicItemComponent), nameof(MagicItemComponent.SetMagicItem))]
        private static class MagicChanges
        {
            private static void Prefix(MagicItemComponent __instance, out string __state) => __state = __instance.Value;
            private static void Postfix(MagicItemComponent __instance, string __state)
            {
                var item = __instance.Item;
                if (item == null || __instance.Value == __state || context?.Action == "craft" || context?.Action == "upgrade" || context?.Action == "refine") return;
                var player = Player.m_localPlayer;
                if (Local(player?.GetInventory()) == null || !player.GetInventory().ContainsItem(item)) return;
                try
                {
                    var magic = __instance.MagicItem;
                    string detail = magic == null ? "not magic" : $"{magic.Rarity}: " + string.Join(", ", magic.Effects.Select(effect => $"{effect.EffectType} {effect.EffectValue:0.##}")) +
                        (magic.Sockets.Count > 0 ? "; sockets " + string.Join(", ", magic.Sockets.Select(socket => socket?.Effect?.EffectType ?? "empty")) : "");
                    string tracking = Track(item, out bool begins);
                    ItemActivityChannel.Record("enchant", Name(item), item.m_stack, item.m_quality, tracking, begins, "EpicLoot", null, detail);
                }
                catch (Exception error) { ItemActivityChannel.Error(error); }
            }
        }

        // Origins: whatever creates a dropped item names it on the object, which keeps it through ownership changes,
        // delayed corpse loot and world saves.
        [HarmonyPatch(typeof(ItemDrop), "Awake"), HarmonyPostfix]
        private static void Stamp(ItemDrop __instance)
        {
            if (!origin && carriedOrigin == null) return;
            var view = DropView(__instance);
            var zdo = view != null && view.IsValid() && view.IsOwner() ? view.GetZDO() : null;
            if (zdo != null && zdo.GetString(OriginKey, null) == null) zdo.Set(OriginKey, ItemActivityRules.Clip(origin ? Origins.Describe(origin) : carriedOrigin, 160));
        }

        // StarLevelSystem spawns creature and resource loot a few items per tick in a coroutine, after the source may be
        // gone, so the coroutine carries the origin described when the loot was created.
        [HarmonyPatch]
        private static class SpreadLoot
        {
            private static bool Prepare() => PluginIds.TryGet(PluginIds.StarLevelSystem, out _);
            private static MethodBase TargetMethod()
            {
                PluginIds.TryGet(PluginIds.StarLevelSystem, out var plugin);
                return AccessTools.Method(plugin.Instance.GetType().Assembly.GetType("StarLevelSystem.modules.Loot.LootPerformanceChanges", true), "DropItemsAsync")
                    ?? throw new MissingMethodException("StarLevelSystem.modules.Loot.LootPerformanceChanges", "DropItemsAsync");
            }
            private static void Postfix(ref IEnumerator __result)
            {
                if (origin) __result = Carry(__result, Origins.Describe(origin));
                else if (carriedOrigin != null) __result = Carry(__result, carriedOrigin);
            }
            private static IEnumerator Carry(IEnumerator spawning, string described)
            {
                while (true)
                {
                    var outer = origin; string outerCarried = carriedOrigin;
                    origin = null; carriedOrigin = described;
                    bool more;
                    try { more = spawning.MoveNext(); }
                    finally { origin = outer; carriedOrigin = outerCarried; }
                    if (!more) yield break;
                    yield return spawning.Current;
                }
            }
        }

        [HarmonyPatch]
        private static class Origins
        {
            private static IEnumerable<MethodBase> TargetMethods() => new[]
            {
                AccessTools.Method(typeof(CharacterDrop), "OnDeath"), AccessTools.Method(typeof(Ragdoll), "SpawnLoot"),
                AccessTools.Method(typeof(Pickable), "RPC_Pick"), AccessTools.Method(typeof(DropOnDestroyed), "OnDestroyed"),
                AccessTools.Method(typeof(MineRock), "RPC_Hit"), AccessTools.Method(typeof(MineRock5), "RPC_Damage"),
                AccessTools.Method(typeof(TreeBase), "RPC_Damage"), AccessTools.Method(typeof(TreeLog), "RPC_Damage"),
                AccessTools.Method(typeof(Beehive), "RPC_Extract"), AccessTools.Method(typeof(Smelter), "Spawn"),
                AccessTools.Method(typeof(CookingStation), "RPC_RemoveDoneItem"), AccessTools.Method(typeof(Fermenter), "RPC_Tap"),
                AccessTools.Method(typeof(SapCollector), "RPC_Extract"), AccessTools.Method(typeof(LootSpawner), "UpdateSpawner"),
                AccessTools.Method(typeof(Humanoid), nameof(Humanoid.DropItem)), AccessTools.Method(typeof(Container), "DropAllItems", Type.EmptyTypes),
            };
            private static void Prefix(Component __instance, out Component __state)
            {
                __state = origin;
                if (ItemActivityChannel.Enabled) origin = __instance;
            }
            private static Exception Finalizer(Component __state, Exception __exception) { origin = __state; return __exception; }

            internal static string Describe(Component source)
            {
                string at = " at " + Position(source.transform.position);
                switch (source)
                {
                    case CharacterDrop drop when drop.GetComponent<Character>() is Character creature:
                        return $"creature {Utils.GetPrefabName(creature.gameObject)} {creature.GetLevel() - 1}*{at}";
                    case Ragdoll ragdoll:
                        var zdo = ragdoll.GetComponent<ZNetView>()?.GetZDO();
                        string name = zdo?.GetString("characterName", "") ?? "";
                        if (name.Length == 0) name = Utils.GetPrefabName(ragdoll.gameObject).Replace("_ragdoll", "");
                        int level = zdo?.GetInt("level", 0) ?? 0;
                        return $"creature {name}{(level > 0 ? $" {level - 1}*" : "")}{at}";
                    case Humanoid humanoid:
                        return $"dropped by {(humanoid is Player player ? player.GetPlayerName() : Utils.GetPrefabName(humanoid.gameObject))}{at}";
                    case Pickable _:
                        return $"harvest {Utils.GetPrefabName(source.gameObject)}{at}";
                    case Beehive _: case Smelter _: case CookingStation _: case Fermenter _: case SapCollector _:
                        return $"production {Utils.GetPrefabName(source.gameObject)}{at}";
                    case Container chest:
                        return "spilled from " + ItemActivity.Describe(chest);
                    case LootSpawner _:
                        return $"spawned loot {Utils.GetPrefabName(source.gameObject)}{at}";
                    default:
                        return $"resource {Utils.GetPrefabName(source.gameObject)}{at}";
                }
            }
        }

        // Loose items that merge on the ground keep one origin only when they share it.
        [HarmonyPatch(typeof(ItemDrop), "AutoStackItems")]
        private static class LooseMerges
        {
            // EpicLoot replaces this method and merges the piles in its own prefix, so the scope opens before it.
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ItemDrop __instance, out string __state)
            {
                var zdo = DropView(__instance)?.GetZDO();
                __state = zdo?.GetString(OriginKey, null);
                merging = __instance; mergedOrigin = __state; mixed = false;
            }
            private static Exception Finalizer(ItemDrop __instance, Exception __exception)
            {
                var zdo = __instance ? DropView(__instance)?.GetZDO() : null;
                if (zdo != null && mixed && DropView(__instance).IsOwner()) zdo.Set(OriginKey, "mixed");
                merging = null;
                return __exception;
            }
        }
        private static ItemDrop merging;
        private static string mergedOrigin;
        private static bool mixed;
        [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.Destroy)), HarmonyPrefix]
        private static void MergedPile(ZNetView __instance)
        {
            if (merging == null) return;
            var drop = __instance.GetComponent<ItemDrop>();
            if (drop && drop != merging && __instance.GetZDO()?.GetString(OriginKey, "unknown") != (mergedOrigin ?? "unknown")) mixed = true;
        }
    }
}
