using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // One Shift action keeps the first selected ingredient, or collects only finished slots. Ingredients come from
    // unprotected carried stacks first, then from eligible chest stacks, skipping favorited items and bag slots; the
    // chosen stack is charged exactly. Native cooking methods still award skills and roll collection bonuses. Additions
    // wait for the owning game's reply; rejected placements return the one charged item to its bag or chest.
    // An owner that does not reply within five seconds ends the batch, and its late reply is ignored: the native game
    // never refunds an addition either, and collected food it spawns stays on the ground. Whoever collects gets the craft
    // credit, on every station: the game gives it to the station's owner, which runs the collection for everyone.
    [HarmonyPatch]
    internal static class BulkCooking
    {
        private const string AddRpc = "ProgressionFixAddCookingItem";
        private const string ReplyRpc = "ProgressionFixCookingReply";
        private const string CollectRpc = "ProgressionFixCollectCookingItem";
        private const string PickupRpc = "ProgressionFixCookingPickup";
        private const string CreditRpc = "ProgressionFixCookingCredit";
        private static readonly Func<CookingStation, Humanoid, bool> Interact = AccessTools.MethodDelegate<Func<CookingStation, Humanoid, bool>>(
            AccessTools.Method(typeof(CookingStation), "OnInteract"));
        private static readonly Func<CookingStation, Humanoid, ItemDrop.ItemData, bool> UseItem =
            AccessTools.MethodDelegate<Func<CookingStation, Humanoid, ItemDrop.ItemData, bool>>(
                AccessTools.Method(typeof(CookingStation), "OnUseItem"));
        private static readonly Func<CookingStation, Inventory, ItemDrop.ItemData> FindIngredient =
            AccessTools.MethodDelegate<Func<CookingStation, Inventory, ItemDrop.ItemData>>(
                AccessTools.Method(typeof(CookingStation), "FindCookableItem"));
        private static readonly Func<CookingStation, bool> FireLit = AccessTools.MethodDelegate<Func<CookingStation, bool>>(
            AccessTools.Method(typeof(CookingStation), "IsFireLit"));
        private static readonly Func<CookingStation, string, bool> ItemAllowed = AccessTools.MethodDelegate<Func<CookingStation, string, bool>>(
            AccessTools.Method(typeof(CookingStation), "IsItemAllowed", new[] { typeof(string) }));
        private static readonly Func<CookingStation, string, bool> ItemDone = AccessTools.MethodDelegate<Func<CookingStation, string, bool>>(
            AccessTools.Method(typeof(CookingStation), "IsItemDone"));
        private static readonly Action<CookingStation, long, string, bool> AddItem =
            AccessTools.MethodDelegate<Action<CookingStation, long, string, bool>>(AccessTools.Method(typeof(CookingStation), "RPC_AddItem"));
        private static readonly Action<CookingStation, long, Vector3, int> RemoveItem =
            AccessTools.MethodDelegate<Action<CookingStation, long, Vector3, int>>(AccessTools.Method(typeof(CookingStation), "RPC_RemoveDoneItem"));
        private static readonly MethodInfo InvokeRpc =
            AccessTools.Method(typeof(ZNetView), nameof(ZNetView.InvokeRPC), new[] { typeof(string), typeof(object[]) });
        private static readonly MethodInfo CraftCredit = AccessTools.Method(typeof(PlayerProfile), nameof(PlayerProfile.IncrementStatItemCraft));
        private static bool running, nativeCall;
        private static int nextRequest;
        private static Placement currentPlacement;
        private static Collection currentCollection;
        private static List<GameObject> collectionOutputs;
        private static ZNetView collectedStation;
        private static long collector;
        private static readonly Dictionary<int, Placement> Pending = new Dictionary<int, Placement>();
        private static readonly Dictionary<int, Collection> PendingCollections = new Dictionary<int, Collection>();
        private static readonly ConditionalWeakTable<CookingStation, Dictionary<long, (int Request, bool Accepted)>> Receipts =
            new ConditionalWeakTable<CookingStation, Dictionary<long, (int, bool)>>();
        private static readonly ConditionalWeakTable<CookingStation, Dictionary<long, (int Request, ZDOID[] Outputs)>> CollectionReceipts =
            new ConditionalWeakTable<CookingStation, Dictionary<long, (int, ZDOID[])>>();

        private sealed class Collection
        {
            internal CookingStation Station;
            internal Player Player;
            internal long Owner;
            internal int Request;
            internal bool Complete;
            // The owner removed nothing, for example because it no longer owned the station.
            internal bool Empty;
        }

        private sealed class Placement
        {
            internal CookingStation Station;
            internal Player Player;
            internal ItemDrop.ItemData Item;
            internal Inventory Inventory;
            internal object Chest;
            internal long Owner;
            internal int Request;
            internal bool? Accepted;
            internal bool AwardSkill;
        }

        internal static bool Supported(CookingStation station)
        {
            if (station == null) return false;
            string prefab = Utils.GetPrefabName(station.gameObject);
            return prefab == "piece_cookingstation" || prefab == "piece_cookingstation_iron" ||
                prefab == "piece_oven" || prefab == "piece_FrostFoundry";
        }

        internal static bool AreaAction(CookingStation station, Humanoid user) => !nativeCall &&
            user == Player.m_localPlayer && Supported(station) && BulkPickup.TryGetHarvestRadius(out float radius) &&
            Nearby(station, radius).Any(candidate => EmptyCount(candidate) > 0);

        private static bool SameGroup(CookingStation target, CookingStation station)
        {
            if (!Supported(station)) return false;
            string targetName = Utils.GetPrefabName(target.gameObject), stationName = Utils.GetPrefabName(station.gameObject);
            return targetName == stationName || (targetName == "piece_cookingstation" || targetName == "piece_cookingstation_iron") &&
                (stationName == "piece_cookingstation" || stationName == "piece_cookingstation_iron");
        }

        // The native selector and AzuCraftyBoxes' chest fallback ignore favorites and charge chests by item name, so
        // a batch picks and later charges one exact unprotected stack itself, in the native recipe order.
        internal static Supply IngredientFor(CookingStation station, Player player, string ingredient)
        {
            var inventory = player.GetInventory();
            var recipes = station.m_conversion.Where(recipe => recipe.m_from && (ingredient == null || recipe.m_from.name == ingredient)).ToList();
            foreach (var recipe in recipes)
            {
                var item = inventory.GetAllItems().FirstOrDefault(candidate => candidate.m_shared.m_name == recipe.m_from.m_itemData.m_shared.m_name &&
                    !FavoriteProtection.IsFavorite(player, candidate));
                if (item != null) return new Supply { Inventory = inventory, Item = item };
            }
            foreach (var recipe in recipes)
            {
                if (!BulkProcessing.CanPull(station, recipe.m_from.name)) continue;
                var chest = BulkProcessing.ChestSupply(station, recipe.m_from.name);
                if (chest.Item != null) return new Supply { Inventory = chest.Inventory, Item = chest.Item, Chest = chest.Chest };
            }
            return null;
        }

        internal sealed class Supply
        {
            internal Inventory Inventory;
            internal ItemDrop.ItemData Item;
            internal object Chest;
        }
        internal static bool Busy => running || Pending.Count > 0 || PendingCollections.Count > 0;
        // Bulk cooking's whole stock of an ingredient: unprotected bag stacks and the chests around every station in reach.
        internal static int Stock(CookingStation target, Player player, string ingredient, float radius) => BulkProcessing.BagStock(player, ingredient, true) +
            BulkProcessing.ChestStock(Nearby(target, radius).Where(station => BulkProcessing.CanPull(station, ingredient)), ingredient, true);
        internal static Supply FirstSupply(CookingStation target, Player player, float radius)
        {
            foreach (var station in Nearby(target, radius))
                if (EmptyCount(station) > 0 && (!station.m_requireFire || FireLit(station)) && IngredientFor(station, player, null) is Supply supply) return supply;
            return null;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(CookingStation), "OnInteract")]
        private static bool OnInteract(CookingStation __instance, Humanoid user, ref bool __result)
        {
            if (nativeCall || user != Player.m_localPlayer || !Supported(__instance) ||
                !BulkPickup.TryGetHarvestRadius(out float radius)) return true;
            if (running || Pending.Count > 0 || PendingCollections.Count > 0) { __result = false; return false; }
            bool collect = FinishedCount(__instance) > 0;
            Supply supply = null;
            bool protectedOnly = false;
            if (!collect)
            {
                supply = FirstSupply(__instance, (Player)user, radius);
                if (supply == null)
                foreach (var station in Nearby(__instance, radius))
                {
                    if (EmptyCount(station) == 0 || station.m_requireFire && !FireLit(station)) continue;
                    protectedOnly |= FindIngredient(station, user.GetInventory()) != null;
                }
            }
            if (!collect && supply == null)
            {
                // Only favorited supplies remain; the ordinary single addition would take one of them.
                if (!protectedOnly) return true;
                user.Message(MessageHud.MessageType.Center, "Only favorited ingredients are available.");
                __result = false;
                return false;
            }
            __result = true;
            ((Player)user).StartCoroutine(Run(__instance, (Player)user, radius, supply?.Item.m_dropPrefab.name, true));
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(CookingStation), "OnUseItem")]
        private static bool OnUseItem(CookingStation __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (nativeCall || user != Player.m_localPlayer || item == null || !Supported(__instance) ||
                !BulkPickup.TryGetHarvestRadius(out float radius) || !ItemAllowed(__instance, item.m_dropPrefab.name)) return true;
            if (running || Pending.Count > 0 || PendingCollections.Count > 0) { __result = false; return false; }
            __result = true;
            ((Player)user).StartCoroutine(Run(__instance, (Player)user, radius, item.m_dropPrefab.name, false));
            return false;
        }

        internal static List<CookingStation> Nearby(CookingStation target, float radius)
        {
            var stations = new HashSet<CookingStation> { target };
            foreach (var collider in Physics.OverlapSphere(target.transform.position, radius))
            {
                var station = collider.GetComponentInParent<CookingStation>();
                if (SameGroup(target, station)) stations.Add(station);
            }
            return stations.OrderBy(station => station == target ? -1f :
                (station.transform.position - target.transform.position).sqrMagnitude).ToList();
        }

        internal static int FinishedCount(CookingStation station)
        {
            var view = station.GetComponent<ZNetView>();
            if (!view.IsValid()) return 0;
            return Enumerable.Range(0, station.m_slots.Length).Count(slot => ItemDone(station, view.GetZDO().GetString("slot" + slot)));
        }

        internal static int EmptyCount(CookingStation station)
        {
            var view = station.GetComponent<ZNetView>();
            if (!view.IsValid()) return 0;
            return Enumerable.Range(0, station.m_slots.Length).Count(slot => view.GetZDO().GetString("slot" + slot) == "");
        }

        private static int FirstFinishedSlot(CookingStation station)
        {
            var view = station.GetComponent<ZNetView>();
            if (!view.IsValid()) return -1;
            for (int slot = 0; slot < station.m_slots.Length; slot++)
                if (ItemDone(station, view.GetZDO().GetString("slot" + slot))) return slot;
            return -1;
        }

        private static bool CanFill(CookingStation station, string ingredient) => station.GetComponent<ZNetView>().IsValid() &&
            ItemAllowed(station, ingredient) && EmptyCount(station) > 0 && (!station.m_requireFire || FireLit(station));

        private static IEnumerator Run(CookingStation target, Player player, float radius, string ingredient, bool awardSkill)
        {
            running = true;
            try
            {
                foreach (var station in Nearby(target, radius))
                {
                    if (station == null || player == null || player.IsDead()) yield break;
                    if (ingredient == null)
                    {
                        for (int item = 0; item < station.m_slots.Length; item++)
                        {
                            int slot = FirstFinishedSlot(station);
                            if (slot < 0) break;
                            var view = station.GetComponent<ZNetView>();
                            string before = view.GetZDO().GetString("slot" + slot);
                            var collection = new Collection { Station = station, Player = player };
                            currentCollection = collection;
                            nativeCall = true;
                            try { Interact(station, player); }
                            finally { nativeCall = false; currentCollection = null; }
                            float deadline = Time.time + 5f;
                            // Another raw item may finish before the reply. Watch the removed slot, not the total.
                            while (station != null && view.IsValid() && !collection.Empty &&
                                (!collection.Complete || view.GetZDO().GetString("slot" + slot) == before) && Time.time < deadline) yield return null;
                            PendingCollections.Remove(collection.Request);
                            if (station == null || !view.IsValid() || collection.Empty || !collection.Complete ||
                                view.GetZDO().GetString("slot" + slot) == before) yield break;
                        }
                        continue;
                    }
                    if (!CanFill(station, ingredient)) continue;
                    // Bound this station's batch by its initial free slots, even before replicated slots arrive.
                    int room = EmptyCount(station);
                    for (int slot = 0; slot < room; slot++)
                    {
                        if (station == null || player == null || player.IsDead() || !CanFill(station, ingredient)) break;
                        var supply = IngredientFor(station, player, ingredient);
                        if (supply == null) break;
                        currentPlacement = new Placement { Station = station, Player = player, AwardSkill = awardSkill,
                            Item = supply.Item, Inventory = supply.Inventory, Chest = supply.Chest };
                        var placement = currentPlacement;
                        bool used;
                        nativeCall = true;
                        try { used = UseItem(station, player, supply.Item); }
                        finally { nativeCall = false; currentPlacement = null; }
                        if (!used || placement.Request == 0) break;
                        float deadline = Time.time + 5f;
                        while (!placement.Accepted.HasValue && Time.time < deadline) yield return null;
                        if (!placement.Accepted.HasValue)
                        {
                            Pending.Remove(placement.Request);
                            player.Message(MessageHud.MessageType.Center, "The cooking station did not respond.");
                            yield break;
                        }
                        if (!placement.Accepted.Value) break;
                        yield return null;
                    }
                }
            }
            finally { running = false; nativeCall = false; currentPlacement = null; currentCollection = null; }
        }

        // A batch's coroutine stops without its cleanup when its player is destroyed, on death or logout. The next local
        // player starts unlocked, and replies to the earlier player's requests are ignored.
        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.SetLocalPlayer))]
        private static void ResetForNewPlayer(Player __instance)
        {
            if (Player.m_localPlayer == __instance) return;
            running = nativeCall = false;
            currentPlacement = null;
            currentCollection = null;
            Pending.Clear();
            PendingCollections.Clear();
        }

        [HarmonyTranspiler, HarmonyPatch(typeof(CookingStation), "OnInteract")]
        private static IEnumerable<CodeInstruction> CollectIntoInventory(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(InvokeRpc), AccessTools.Method(typeof(BulkCooking), nameof(SendCollection)), 1,
                "CookingStation's remove-item RPC changed.");

        private static void SendCollection(ZNetView view, string method, object[] arguments)
        {
            if (currentCollection == null) { view.InvokeRPC(method, arguments); return; }
            if (view.GetZDO().GetOwner() == 0L) view.ClaimOwnership();
            currentCollection.Owner = view.GetZDO().GetOwner();
            int request = currentCollection.Request = ++nextRequest;
            PendingCollections.Add(request, currentCollection);
            view.InvokeRPC(currentCollection.Owner, CollectRpc, request, (Vector3)arguments[0], (int)arguments[1]);
        }

        [HarmonyTranspiler, HarmonyPatch(typeof(CookingStation), "SpawnItem")]
        private static IEnumerable<CodeInstruction> CaptureOutputs(IEnumerable<CodeInstruction> instructions) =>
            HarvestOutputPickup.CaptureInstantiate<GameObject>(instructions, AccessTools.Method(typeof(BulkCooking), nameof(RememberOutput)),
                "CookingStation's output creation changed.");

        private static void RememberOutput(GameObject output) => collectionOutputs?.Add(output);

        [HarmonyPrefix, HarmonyPatch(typeof(CookingStation), "RPC_RemoveDoneItem")]
        private static void RememberCollector(ZNetView ___m_nview, long sender)
        {
            collectedStation = ___m_nview;
            collector = sender;
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(CookingStation), "RPC_RemoveDoneItem")]
        private static void ForgetCollector()
        {
            collectedStation = null;
            collector = 0L;
        }

        [HarmonyTranspiler, HarmonyPatch(typeof(CookingStation), "SpawnItem")]
        private static IEnumerable<CodeInstruction> CreditCollector(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(CraftCredit), AccessTools.Method(typeof(BulkCooking), nameof(Credit)), 1,
                "CookingStation's craft credit changed.");

        private static void Credit(PlayerProfile profile, string item, float amount, bool cheated)
        {
            if (collector == 0L || collector == ZDOMan.GetSessionID()) profile.IncrementStatItemCraft(item, amount, cheated);
            else collectedStation.InvokeRPC(collector, CreditRpc, item);
        }

        private static void ReceiveCollection(CookingStation station, ZNetView view, long sender, int request, Vector3 point, int amount) =>
            HarvestOutputPickup.ReceiveCollection(view, CollectionReceipts.GetOrCreateValue(station), sender, request, PickupRpc, outputs =>
            {
                collectionOutputs = outputs;
                try { RemoveItem(station, sender, point, amount); }
                finally { collectionOutputs = null; }
            });

        private static void ReceivePickup(CookingStation station, long sender, int request, ZPackage package)
        {
            if (!PendingCollections.TryGetValue(request, out var collection) || collection.Station != station || collection.Owner != sender) return;
            var ids = HarvestOutputPickup.ReadIds(package);
            PendingCollections.Remove(request);
            if (ids.Count == 0) { collection.Empty = true; return; }
            collection.Player.StartCoroutine(PickupOutputs(collection, ids));
        }

        private static IEnumerator PickupOutputs(Collection collection, List<ZDOID> ids)
        {
            var pickup = HarvestOutputPickup.Pickup(collection.Player, ids);
            while (pickup.MoveNext()) yield return pickup.Current;
            collection.Complete = true;
        }


        [HarmonyTranspiler, HarmonyPatch(typeof(CookingStation), "CookItem")]
        private static IEnumerable<CodeInstruction> ConfirmAdditions(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(InvokeRpc), AccessTools.Method(typeof(BulkCooking), nameof(SendAddition)), 1,
                "CookingStation's add-item RPC changed.");

        // Native CookItem has just charged a carried stack. A chest stack is charged here, at the same point, after the
        // native checks passed and immediately before the addition is sent; a stack that changed meanwhile sends nothing.
        private static void SendAddition(ZNetView view, string method, object[] arguments)
        {
            if (currentPlacement == null) { view.InvokeRPC(method, arguments); return; }
            var placement = currentPlacement;
            var stack = placement.Item;
            if (placement.Chest != null)
            {
                if (FavoriteProtection.IsFavoriteInStorage(stack) || !placement.Inventory.RemoveOneItem(stack)) return;
                BulkProcessing.SaveChest(placement.Chest);
            }
            placement.Item = stack.Clone();
            placement.Item.m_stack = 1;
            placement.Owner = view.GetZDO().GetOwner();
            int request = placement.Request = ++nextRequest;
            Pending.Add(request, placement);
            view.InvokeRPC(placement.Owner, AddRpc, request, (string)arguments[0], (bool)arguments[1]);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(CookingStation), "Awake")]
        private static void RegisterReplies(CookingStation __instance, ZNetView ___m_nview)
        {
            if (!___m_nview.IsValid()) return;
            ___m_nview.Register<string>(CreditRpc, (sender, item) => Game.instance.GetPlayerProfile().IncrementStatItemCraft(item));
            if (!Supported(__instance)) return;
            ___m_nview.Register<int, string, bool>(AddRpc, (sender, request, ingredient, cheated) =>
                ReceiveAddition(__instance, ___m_nview, sender, request, ingredient, cheated));
            ___m_nview.Register<int, bool>(ReplyRpc, (sender, request, accepted) =>
                ReceiveReply(__instance, sender, request, accepted));
            ___m_nview.Register<int, Vector3, int>(CollectRpc, (sender, request, point, amount) =>
                ReceiveCollection(__instance, ___m_nview, sender, request, point, amount));
            ___m_nview.Register<int, ZPackage>(PickupRpc, (sender, request, package) =>
                ReceivePickup(__instance, sender, request, package));
        }

        private static void ReceiveAddition(CookingStation station, ZNetView view, long sender, int request, string ingredient, bool cheated)
        {
            var receipts = Receipts.GetOrCreateValue(station);
            if (receipts.TryGetValue(sender, out var previous) && request <= previous.Request)
            {
                if (request == previous.Request) view.InvokeRPC(sender, ReplyRpc, request, previous.Accepted);
                return;
            }
            // OnUseItem already applies the caller's native fire rule. The native receiving RPC validates recipe
            // and space only; preserve that split, including client-side fire mods.
            bool accepted = view.IsOwner() && view.IsValid() && ItemAllowed(station, ingredient) && EmptyCount(station) > 0;
            if (accepted) AddItem(station, sender, ingredient, cheated);
            receipts[sender] = (request, accepted);
            view.InvokeRPC(sender, ReplyRpc, request, accepted);
        }

        private static void ReceiveReply(CookingStation station, long sender, int request, bool accepted)
        {
            if (!Pending.TryGetValue(request, out var placement) || placement.Station != station || placement.Owner != sender) return;
            Pending.Remove(request);
            placement.Accepted = accepted;
            if (accepted)
            {
                if (placement.AwardSkill && placement.Player != null && station.m_skill != Skills.SkillType.None)
                    placement.Player.RaiseSkill(station.m_skill, 0.4f);
                return;
            }
            if (placement.Inventory.AddItem(placement.Item))
            {
                if (placement.Chest != null) BulkProcessing.SaveChest(placement.Chest);
            }
            else ItemDrop.DropItem(placement.Item, 1, placement.Player.transform.position + Vector3.up, Quaternion.identity);
        }
    }
}
