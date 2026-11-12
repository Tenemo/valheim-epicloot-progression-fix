using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class BulkProcessing
    {
        private const string AddRpc = "ProgressionFixMachineAdd";
        private const string ReplyRpc = "ProgressionFixMachineReply";
        private const string CollectRpc = "ProgressionFixMachineCollect";
        private const string PickupRpc = "ProgressionFixMachinePickup";
        private static readonly Func<Smelter, string, bool> ItemAllowed = AccessTools.MethodDelegate<Func<Smelter, string, bool>>(
            AccessTools.Method(typeof(Smelter), "IsItemAllowed", new[] { typeof(string) }));
        private static readonly Action<Smelter, long, string, bool> AddOre = AccessTools.MethodDelegate<Action<Smelter, long, string, bool>>(
            AccessTools.Method(typeof(Smelter), "RPC_AddOre"));
        private static readonly Action<Smelter, long> AddFuel = AccessTools.MethodDelegate<Action<Smelter, long>>(
            AccessTools.Method(typeof(Smelter), "RPC_AddFuel"));
        private static readonly Action<CookingStation, long> AddCookingFuel = AccessTools.MethodDelegate<Action<CookingStation, long>>(
            AccessTools.Method(typeof(CookingStation), "RPC_AddFuel"));
        private static readonly Action<Smelter, long> EmptyProcessed = AccessTools.MethodDelegate<Action<Smelter, long>>(
            AccessTools.Method(typeof(Smelter), "RPC_EmptyProcessed"));
        private static readonly AccessTools.FieldRef<Smelter, float> AddedOreTime = AccessTools.FieldRefAccess<Smelter, float>("m_addedOreTime");
        private static readonly Action<Smelter, bool> Animate = AccessTools.MethodDelegate<Action<Smelter, bool>>(
            AccessTools.Method(typeof(Smelter), "SetAnimation"));
        private static readonly ConditionalWeakTable<Component, Dictionary<long, (int Request, bool Accepted)>> Receipts =
            new ConditionalWeakTable<Component, Dictionary<long, (int, bool)>>();
        private static readonly ConditionalWeakTable<Smelter, Dictionary<long, (int Request, ZDOID[] Outputs)>> CollectionReceipts =
            new ConditionalWeakTable<Smelter, Dictionary<long, (int, ZDOID[])>>();
        private static readonly Dictionary<int, Addition> Pending = new Dictionary<int, Addition>();
        private static readonly Dictionary<int, Collection> PendingCollections = new Dictionary<int, Collection>();
        private static MethodInfo query, chestInventory, chestName, saveChest;
        private static Func<bool> pullingPrevented;
        private static Func<string, string, bool> canPull;
        private static Func<int, int> leaveOne;
        private static ConfigEntry<float> supplyRange;
        private static ConfigEntry<KeyboardShortcut> fillAllKey;
        private static bool running;
        private static int nextRequest;
        private static List<GameObject> collectionOutputs;

        internal sealed class Supply
        {
            internal Inventory Inventory;
            internal ItemDrop.ItemData Item;
            internal object Chest;
        }

        private sealed class Addition
        {
            internal Component Station;
            internal Supply Source;
            internal ItemDrop.ItemData Item;
            internal long Owner;
            internal Vector3 ReturnPosition;
            internal bool Fuel;
            internal bool? Accepted;
        }

        private sealed class Collection
        {
            internal Smelter Station;
            internal long Owner;
            internal List<ZDOID> Outputs;
        }

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var boxes = assembly.GetType("AzuCraftyBoxes.Util.Functions.Boxes", true);
            query = AccessTools.Method(AccessTools.Inner(boxes, "QueryFrame"), "Get").MakeGenericMethod(typeof(Component));
            var container = assembly.GetType("AzuCraftyBoxes.IContainers.IContainer", true);
            chestInventory = AccessTools.Method(container, "GetInventory");
            chestName = AccessTools.Method(container, "GetPrefabName");
            saveChest = AccessTools.Method(container, "Save");
            canPull = AccessTools.MethodDelegate<Func<string, string, bool>>(AccessTools.Method(
                assembly.GetType("AzuCraftyBoxes.API", true), "CanItemBePulled", new[] { typeof(string), typeof(string) }));
            leaveOne = AccessTools.MethodDelegate<Func<int, int>>(AccessTools.Method(boxes, "CheckAndDecrement"));
            pullingPrevented = AccessTools.MethodDelegate<Func<bool>>(AccessTools.Method(
                assembly.GetType("AzuCraftyBoxes.Util.Functions.MiscFunctions", true), "ShouldPrevent"));
            fillAllKey = (ConfigEntry<KeyboardShortcut>)AccessTools.Field(plugin.Instance.GetType(), "fillAllModKey")?.GetValue(null);
            if (chestInventory == null || chestName == null || saveChest == null || fillAllKey == null ||
                !plugin.Instance.Config.TryGetEntry("2 - CraftyBoxes", "Container Range", out supplyRange))
                throw new InvalidOperationException("AzuCraftyBoxes' machine supply changed.");
            var hints = assembly.GetType("AzuCraftyBoxes.Patches.OverrideHoverText", true);
            foreach (string method in new[] { "UpdateAddOreSwitchHoverText", "UpdateAddWoodSwitchHoverText" })
                harmony.Patch(AccessTools.Method(hints, method), prefix: new HarmonyMethod(typeof(BulkProcessing), nameof(KeepSingleMachineHint)));
            foreach (string type in new[] { "SmelterOnAddOrePatch", "SmelterOnAddFuelPatch", "CookingStationOnAddFuelSwitchPatch" })
                harmony.Patch(AccessTools.Method(assembly.GetType("AzuCraftyBoxes.Patches." + type, true), "Prefix"),
                    prefix: new HarmonyMethod(typeof(BulkProcessing), nameof(SkipSingleMachineSupply)));
        }

        private static bool KeepSingleMachineHint(Smelter __0) => !Supported(__0) || !BulkPickup.TryGetHarvestRadius(out _, true) ||
            fillAllKey.Value.MainKey != BulkPickup.Modifier;

        private static bool SkipSingleMachineSupply(Component __0, Humanoid user, ref bool __result)
        {
            // An Ashlands hot tub takes no wood, so nearby chests keep theirs too.
            if (__0 is Smelter tub && AshlandsHotTubs.Heated(tub)) { __result = true; return false; }
            if (user != Player.m_localPlayer || !Supported(__0) || !BulkPickup.TryGetHarvestRadius(out _)) return true;
            __result = true;
            return false;
        }

        internal static bool Supported(Component station)
        {
            if (!station) return false;
            switch (Utils.GetPrefabName(station.gameObject))
            {
                case "piece_spinningwheel": case "windmill": case "smelter": case "blastfurnace":
                case "charcoal_kiln": case "piece_FrostKiln": case "eitrrefinery": return station is Smelter;
                case "piece_oven": case "piece_FrostFoundry": return station is CookingStation;
                default: return false;
            }
        }

        internal static bool AreaAction(Smelter station, Humanoid user) => user == Player.m_localPlayer &&
            Supported(station) && BulkPickup.TryGetHarvestRadius(out float radius) && Nearby(station, radius).Any(candidate => Room(candidate, false) > 0);

        [HarmonyPrefix, HarmonyPatch(typeof(Switch), nameof(Switch.Interact))]
        private static bool OneOperation(Switch __instance, Humanoid character, bool hold, ref bool __result)
        {
            if (!hold || character != Player.m_localPlayer || !BulkPickup.TryGetHarvestRadius(out _)) return true;
            var station = (Component)__instance.GetComponentInParent<Smelter>() ?? __instance.GetComponentInParent<CookingStation>();
            if (!Supported(station)) return true;
            __result = false;
            return false;
        }

        internal static ItemDrop Fuel(Component station) => station is Smelter smelter ? smelter.m_fuelItem : ((CookingStation)station).m_fuelItem;

        internal static int Room(Component station, bool fuel)
        {
            var view = station.GetComponentInParent<ZNetView>();
            if (!view || !view.IsValid()) return 0;
            if (!fuel) return station is Smelter machine ? Math.Max(0, machine.m_maxOre - view.GetZDO().GetInt(ZDOVars.s_queued)) : 0;
            int maximum = station is Smelter smelter ? smelter.m_maxFuel : ((CookingStation)station).m_maxFuel;
            return Math.Max(0, Mathf.FloorToInt(maximum - view.GetZDO().GetFloat(ZDOVars.s_fuel)));
        }

        internal static List<Component> Nearby(Component target, float radius)
        {
            var stations = new HashSet<Component> { target };
            string name = Utils.GetPrefabName(target.gameObject);
            foreach (var collider in Physics.OverlapSphere(target.transform.position, radius))
            {
                var station = collider.GetComponentInParent(target.GetType());
                if (station && Utils.GetPrefabName(station.gameObject) == name) stations.Add(station);
            }
            return stations.OrderBy(station => station == target ? -1f : (station.transform.position - target.transform.position).sqrMagnitude).ToList();
        }

        // Bulk loading never takes favorited items or items in the player's protected slots. Chest cells only
        // follow item favorites, since they do not inherit the player's slot coordinates.
        internal static Supply FindSupply(Component station, Player player, ItemDrop ingredient, bool fuel, bool inventoryOnly = false)
        {
            string prefab = ingredient.name;
            if (!canPull(Utils.GetPrefabName(station.gameObject), prefab)) return null;
            var inventory = player.GetInventory();
            var item = inventory.GetAllItems().FirstOrDefault(candidate => candidate.m_dropPrefab && candidate.m_dropPrefab.name == prefab &&
                !FavoriteProtection.IsFavorite(player, candidate));
            if (item != null) return new Supply { Inventory = inventory, Item = item };
            if (inventoryOnly) return null;
            var chest = ChestSupply(fuel ? station : player, prefab);
            return chest.Item == null ? null : new Supply { Inventory = chest.Inventory, Item = chest.Item, Chest = chest.Chest };
        }

        // The first unprotected stack in a nearby chest that AzuCraftyBoxes would let the player pull from.
        internal static (Inventory Inventory, ItemDrop.ItemData Item, object Chest) ChestSupply(Component origin, string prefab) =>
            ChestSupply(origin, new[] { prefab }, true);

        // Single fermenter loading retains the native chest-first recipe order and permits deliberately selected favorites.
        internal static (Inventory Inventory, ItemDrop.ItemData Item, object Chest) ChestSupply(Component origin, string[] prefabs, bool protectFavorites)
        {
            if (query == null || pullingPrevented()) return default;
            foreach (var chest in (IEnumerable)query.Invoke(null, new object[] { origin, supplyRange.Value }))
            foreach (string prefab in prefabs)
            {
                if (!canPull((string)chestName.Invoke(chest, null), prefab)) continue;
                var inventory = (Inventory)chestInventory.Invoke(chest, null);
                if (inventory == null) continue;
                var eligible = inventory.GetAllItems().Where(candidate => candidate.m_dropPrefab && candidate.m_dropPrefab.name == prefab &&
                    (!protectFavorites || !FavoriteProtection.IsFavoriteInStorage(candidate))).ToList();
                if (eligible.Count == 0 || leaveOne(eligible.Sum(candidate => candidate.m_stack)) <= 0) continue;
                return (inventory, eligible[0], chest);
            }
            return default;
        }

        // Bag stacks of an item; without favorites and protected slots for the actions that protect them.
        internal static int BagStock(Player player, string prefab, bool protectFavorites) => player.GetInventory().GetAllItems()
            .Where(item => item.m_dropPrefab && item.m_dropPrefab.name == prefab && (!protectFavorites || !FavoriteProtection.IsFavorite(player, item))).Sum(item => item.m_stack);

        // What the chests around these origins may give of an item, counting each chest once even where several stations
        // share it: what AzuCraftyBoxes would release, without favorited stacks for the actions that protect them.
        internal static int ChestStock(IEnumerable<Component> origins, string prefab, bool protectFavorites)
        {
            if (query == null || pullingPrevented()) return 0;
            var counted = new HashSet<Inventory>(); int total = 0;
            foreach (var origin in origins)
            foreach (var chest in (IEnumerable)query.Invoke(null, new object[] { origin, supplyRange.Value }))
            {
                var inventory = (Inventory)chestInventory.Invoke(chest, null);
                if (inventory == null || !counted.Add(inventory) || !canPull((string)chestName.Invoke(chest, null), prefab)) continue;
                total += Math.Max(0, leaveOne(inventory.GetAllItems().Where(item => item.m_dropPrefab && item.m_dropPrefab.name == prefab &&
                    (!protectFavorites || !FavoriteProtection.IsFavoriteInStorage(item))).Sum(item => item.m_stack)));
            }
            return total;
        }

        // Bulk loading's whole stock of an ingredient: ingredients come from around the player, fuel from around each machine.
        internal static int Stock(Component station, Player player, ItemDrop ingredient, bool fuel, float radius) => !CanPull(station, ingredient.name) ? 0 :
            BagStock(player, ingredient.name, true) + ChestStock(fuel ? Nearby(station, radius) : new List<Component> { player }, ingredient.name, true);

        internal static bool CanPull(Component station, string prefab) => canPull != null && canPull(Utils.GetPrefabName(station.gameObject), prefab);
        internal static void SaveChest(object chest) => saveChest.Invoke(chest, null);
        internal static bool Busy => running;
        internal static ItemDrop SelectIngredient(Component station, Player player, bool fuel)
        {
            if (fuel) return Fuel(station);
            var choices = ((Smelter)station).m_conversion.Where(recipe => recipe.m_from && ItemAllowed((Smelter)station, recipe.m_from.name)).Select(recipe => recipe.m_from).ToList();
            return choices.FirstOrDefault(choice => FindSupply(station, player, choice, false, true) != null) ??
                choices.FirstOrDefault(choice => FindSupply(station, player, choice, false) != null);
        }

        private static bool Begin(Component station, Humanoid user, ItemDrop.ItemData explicitItem, bool fuel, ref bool result)
        {
            if (user != Player.m_localPlayer || !Supported(station) || !BulkPickup.TryGetHarvestRadius(out float radius)) return true;
            result = false;
            if (running) return false;
            if (!Nearby(station, radius).Any(candidate => Room(candidate, fuel) > 0)) return true;
            var player = (Player)user;
            ItemDrop ingredient = fuel ? Fuel(station) : explicitItem?.m_dropPrefab.GetComponent<ItemDrop>();
            if (explicitItem != null && (ingredient == null || fuel && explicitItem.m_dropPrefab.name != ingredient.name ||
                !fuel && !ItemAllowed((Smelter)station, ingredient.name))) return true;
            if (ingredient == null)
                ingredient = SelectIngredient(station, player, fuel);
            if (ingredient == null || !canPull(Utils.GetPrefabName(station.gameObject), ingredient.name))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_noprocessableitems");
                return false;
            }
            result = true;
            player.StartCoroutine(Fill(station, player, ingredient, fuel, radius));
            return false;
        }

        [HarmonyPrefix, HarmonyPriority(Priority.First - 1), HarmonyPatch(typeof(Smelter), "OnAddOre")]
        private static bool Ore(Smelter __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result, bool __runOriginal) =>
            !__runOriginal || Begin(__instance, user, item, false, ref __result);

        [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyPatch(typeof(Smelter), "OnAddFuel")]
        private static bool FuelSwitch(Smelter __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result, bool __runOriginal) =>
            !__runOriginal || Begin(__instance, user, item, true, ref __result);

        [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyPatch(typeof(CookingStation), "OnAddFuelSwitch")]
        private static bool CookingFuel(CookingStation __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result, bool __runOriginal) =>
            !__runOriginal || Begin(__instance, user, item, true, ref __result);

        private static IEnumerator Fill(Component target, Player player, ItemDrop ingredient, bool fuel, float radius)
        {
            running = true;
            try
            {
                foreach (var station in Nearby(target, radius))
                {
                    int limit = Room(station, fuel);
                    for (int index = 0; index < limit; index++)
                    {
                        if (!station || !player || player.IsDead() || Room(station, fuel) == 0 ||
                            !fuel && !ItemAllowed((Smelter)station, ingredient.name)) break;
                        var source = FindSupply(station, player, ingredient, fuel);
                        if (source == null) break;
                        var view = station.GetComponent<ZNetView>();
                        if (view.GetZDO().GetOwner() == 0L) view.ClaimOwnership();
                        var addition = new Addition { Station = station, Source = source, Item = source.Item.Clone(),
                            Owner = view.GetZDO().GetOwner(), ReturnPosition = player.transform.position + Vector3.up, Fuel = fuel };
                        addition.Item.m_stack = 1;
                        if (!source.Inventory.RemoveItem(source.Item, 1)) break;
                        if (source.Chest != null) saveChest.Invoke(source.Chest, null);
                        int request = ++nextRequest;
                        Pending.Add(request, addition);
                        view.InvokeRPC(addition.Owner, AddRpc, request, ingredient.name, fuel, addition.Item.m_cheated);
                        float deadline = Time.time + 5f;
                        while (!addition.Accepted.HasValue && Time.time < deadline) yield return null;
                        Pending.Remove(request);
                        if (!addition.Accepted.HasValue)
                        {
                            if (player) player.Message(MessageHud.MessageType.Center, "The machine did not respond.");
                            yield break;
                        }
                        if (!addition.Accepted.Value) break;
                        yield return null;
                    }
                }
            }
            finally { running = false; }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Smelter), "Awake")]
        private static void RegisterSmelter(Smelter __instance, ZNetView ___m_nview)
        {
            Register(__instance, ___m_nview);
            if (Utils.GetPrefabName(__instance.gameObject) != "windmill" || !___m_nview.IsValid()) return;
            ___m_nview.Register<int>(CollectRpc, (sender, request) => HarvestOutputPickup.ReceiveCollection(___m_nview,
                CollectionReceipts.GetOrCreateValue(__instance), sender, request, PickupRpc, outputs =>
                {
                    collectionOutputs = outputs;
                    try { EmptyProcessed(__instance, sender); }
                    finally { collectionOutputs = null; }
                }));
            ___m_nview.Register<int, ZPackage>(PickupRpc, (sender, request, package) =>
            {
                if (PendingCollections.TryGetValue(request, out var collection) && collection.Station == __instance && collection.Owner == sender)
                    collection.Outputs = HarvestOutputPickup.ReadIds(package);
            });
        }

        [HarmonyPostfix, HarmonyPatch(typeof(CookingStation), "Awake")]
        private static void RegisterCooking(CookingStation __instance, ZNetView ___m_nview) => Register(__instance, ___m_nview);

        private static void Register(Component station, ZNetView view)
        {
            if (!Supported(station) || !view || !view.IsValid()) return;
            view.Register<int, string, bool, bool>(AddRpc, (sender, request, ingredient, fuel, cheated) => ReceiveAddition(station, view, sender, request, ingredient, fuel, cheated));
            view.Register<int, bool>(ReplyRpc, (sender, request, accepted) =>
            {
                if (!Pending.TryGetValue(request, out var addition) || addition.Station != station || addition.Owner != sender) return;
                Pending.Remove(request);
                addition.Accepted = accepted;
                if (accepted)
                {
                    if (!addition.Fuel && station is Smelter smelter)
                    {
                        AddedOreTime(smelter) = Time.time;
                        if (smelter.m_addOreAnimationDuration > 0f) Animate(smelter, true);
                    }
                    return;
                }
                if (addition.Source.Inventory.AddItem(addition.Item))
                {
                    if (addition.Source.Chest != null) saveChest.Invoke(addition.Source.Chest, null);
                }
                else ItemDrop.DropItem(addition.Item, 1, addition.ReturnPosition, Quaternion.identity);
            });
        }

        private static void ReceiveAddition(Component station, ZNetView view, long sender, int request, string ingredient, bool fuel, bool cheated)
        {
            var receipts = Receipts.GetOrCreateValue(station);
            if (receipts.TryGetValue(sender, out var previous) && request <= previous.Request)
            {
                if (request == previous.Request) view.InvokeRPC(sender, ReplyRpc, request, previous.Accepted);
                return;
            }
            int before = view.IsValid() ? Room(station, fuel) : 0;
            bool accepted = view.IsValid() && view.IsOwner() && before > 0 &&
                (fuel ? Fuel(station) && Fuel(station).name == ingredient : station is Smelter smelter && ItemAllowed(smelter, ingredient));
            if (accepted)
            {
                if (!fuel) AddOre((Smelter)station, sender, ingredient, cheated);
                else if (station is Smelter fuelMachine) AddFuel(fuelMachine, sender);
                else AddCookingFuel((CookingStation)station, sender);
                accepted = Room(station, fuel) < before;
            }
            receipts[sender] = (request, accepted);
            view.InvokeRPC(sender, ReplyRpc, request, accepted);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Smelter), "OnEmpty")]
        private static bool Collect(Smelter __instance, Humanoid user, ref bool __result)
        {
            if (user != Player.m_localPlayer || Utils.GetPrefabName(__instance.gameObject) != "windmill" ||
                !BulkPickup.TryGetHarvestRadius(out float radius)) return true;
            __result = !running;
            if (!running) ((Player)user).StartCoroutine(CollectNearby(__instance, (Player)user, radius));
            return false;
        }

        private static IEnumerator CollectNearby(Smelter target, Player player, float radius)
        {
            running = true;
            try
            {
                foreach (Smelter station in Nearby(target, radius))
                {
                    if (!station || !player || player.IsDead()) yield break;
                    var view = station.GetComponent<ZNetView>();
                    if (!view || !view.IsValid()) continue;
                    if (view.GetZDO().GetOwner() == 0L) view.ClaimOwnership();
                    int request = ++nextRequest;
                    var collection = new Collection { Station = station, Owner = view.GetZDO().GetOwner() };
                    PendingCollections.Add(request, collection);
                    view.InvokeRPC(collection.Owner, CollectRpc, request);
                    float deadline = Time.time + 5f;
                    while (collection.Outputs == null && Time.time < deadline) yield return null;
                    PendingCollections.Remove(request);
                    if (collection.Outputs == null) yield break;
                    yield return HarvestOutputPickup.Pickup(player, collection.Outputs);
                }
            }
            finally { running = false; }
        }

        [HarmonyTranspiler, HarmonyPatch(typeof(Smelter), "Spawn")]
        private static IEnumerable<CodeInstruction> CaptureOutputs(IEnumerable<CodeInstruction> instructions) =>
            HarvestOutputPickup.CaptureInstantiate<GameObject>(instructions, AccessTools.Method(typeof(BulkProcessing), nameof(RememberOutput)),
                "Smelter's output creation changed.");

        private static void RememberOutput(GameObject output) => collectionOutputs?.Add(output);

        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.SetLocalPlayer))]
        private static void ResetForNewPlayer(Player __instance)
        {
            if (Player.m_localPlayer == __instance) return;
            running = false;
            Pending.Clear();
            PendingCollections.Clear();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Smelter), "OnHoverEmptyOre")]
        private static void CollectHint(Smelter __instance, ref string __result)
        { if (Utils.GetPrefabName(__instance.gameObject) == "windmill") __result += BulkPickup.Hint("Collect finished output nearby"); }

    }
}
