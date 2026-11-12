using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // One request pays for one barrel. Batches keep one prefab and wait for its owner's reply before continuing.
    // Each payment is saved with the character before its request leaves, and the owner saves each outcome with the
    // barrel, so a lost request or reply is settled whenever the barrel is loaded again, also after a reconnect, an
    // ownership change or a server restart: an accepted base stays in the barrel, a refused one returns once, and one
    // whose outcome is unknown stays on record.
    [HarmonyPatch]
    internal static class BulkFermenting
    {
        private const string PlaceRpc = "ProgressionFixFermenterPlace", ReceiptRpc = "ProgressionFixFermenterReceipt";
        // A payment's character data key ends with its barrel's identity and its own, in hexadecimal.
        private const string PaymentKey = "ProgressionFix.FermenterPayment.";
        private const int ReceiptLimit = 32;
        private static readonly int IdentityKey = "ProgressionFix.FermenterIdentity".GetStableHashCode();
        private static readonly int ReceiptsKey = "ProgressionFix.FermenterReceipts".GetStableHashCode();
        private static readonly Func<Fermenter, int> Content = AccessTools.MethodDelegate<Func<Fermenter, int>>(AccessTools.Method(typeof(Fermenter), "GetContent"));
        private static readonly Func<Fermenter, double> Elapsed = AccessTools.MethodDelegate<Func<Fermenter, double>>(AccessTools.Method(typeof(Fermenter), "GetFermentationTime"));
        private static readonly Action<Fermenter, long, int, bool> AddItem = AccessTools.MethodDelegate<Action<Fermenter, long, int, bool>>(AccessTools.Method(typeof(Fermenter), "RPC_AddItem"));
        private static readonly Action<Fermenter, float, bool> UpdateCover = AccessTools.MethodDelegate<Action<Fermenter, float, bool>>(AccessTools.Method(typeof(Fermenter), "UpdateCover"));
        // This session's requests by transaction, until their outcome arrives.
        private static readonly Dictionary<long, Placement> Placements = new Dictionary<long, Placement>();
        private static bool running, tapping;
        private static int playerGeneration;

        internal sealed class Supply
        {
            internal ItemDrop.ItemData Item;
            internal Inventory Inventory;
            internal object Chest;
        }
        // A payment as the character's save keeps it.
        private sealed class Payment
        {
            internal long Barrel, Transaction, World;
            internal string Prefab;
            internal bool Cheated, Rejected;
            internal ItemDrop.ItemData Item;
        }
        // A request of this session. Source is the waiting batch's supply, which a refusal refills; it is gone once the
        // batch stopped waiting, so a later refusal goes to the player.
        private sealed class Placement
        {
            internal Supply Source;
            internal ItemDrop.ItemData Paid;
            internal Vector3 ReturnPosition;
            internal float Sent;
            internal bool? Accepted;
        }

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var plugin)) return;
            var writer = plugin.Instance.GetType().Assembly.GetType("AzuCraftyBoxes.Patches.OverrideHoverTextFermenter", true);
            harmony.Patch(AccessTools.Method(writer, "UpdateAddSwitchHoverText"), prefix: new HarmonyMethod(typeof(BulkFermenting), nameof(ReplaceList)));
        }
        private static bool ReplaceList() => false;
        private static bool Ready(Fermenter station) => Content(station) != 0 && Elapsed(station) > station.m_fermentationDuration;
        private static bool Allowed(Fermenter station, string prefab) => station.m_conversion.Any(recipe => recipe.m_from && recipe.m_from.name == prefab);

        // UpdateCover also resets fermentation timestamps. Previewing must only read the same physical cover.
        private static string Unavailable(Fermenter station, long playerId)
        {
            var view = station ? station.GetComponent<ZNetView>() : null;
            if (!view || !view.IsValid()) return "Fermenter unavailable";
            if (!WardAccess.Allowed(station, playerId)) return "$piece_noaccess";
            if (Content(station) != 0) return "Fermenter is occupied";
            Cover.GetCoverForPoint(station.m_roofCheckPoint.position, out float cover, out bool roof);
            if (!roof) return "$piece_fermenter_needroof";
            return cover < .7f ? "$piece_fermenter_exposed" : null;
        }

        internal static Supply Select(Fermenter station, Player player, bool bulk, string prefab = null)
        {
            var recipes = station.m_conversion.Where(recipe => recipe.m_from && (prefab == null || recipe.m_from.name == prefab)).ToArray();
            var inventory = player.GetInventory();
            foreach (var recipe in recipes)
            {
                var item = bulk ? inventory.GetAllItems().FirstOrDefault(candidate => candidate.m_dropPrefab && candidate.m_dropPrefab.name == recipe.m_from.name &&
                    candidate.m_stack > 0 && !FavoriteProtection.IsFavorite(player, candidate)) : inventory.GetItem(recipe.m_from.m_itemData.m_shared.m_name);
                if (item != null && item.m_stack > 0 && item.m_dropPrefab && Allowed(station, item.m_dropPrefab.name) && (prefab == null || item.m_dropPrefab.name == prefab))
                    return new Supply { Inventory = inventory, Item = item };
            }
            var prefabs = recipes.Select(recipe => recipe.m_from.name).Where(name => BulkProcessing.CanPull(station, name)).ToArray();
            if (!bulk)
            {
                var source = BulkProcessing.ChestSupply(station, prefabs, false);
                return source.Item == null ? null : new Supply { Inventory = source.Inventory, Item = source.Item, Chest = source.Chest };
            }
            foreach (string name in prefabs)
            {
                var source = BulkProcessing.ChestSupply(station, name);
                if (source.Item != null) return new Supply { Inventory = source.Inventory, Item = source.Item, Chest = source.Chest };
            }
            return null;
        }

        // As for machines, a barrel counts once any of its shapes reaches the sphere, including its native base area.
        private static List<Fermenter> Nearby(Fermenter target, float radius)
        {
            var found = new HashSet<Fermenter> { target };
            foreach (var collider in Physics.OverlapSphere(target.transform.position, radius))
            {
                var station = collider.GetComponentInParent<Fermenter>();
                if (station) found.Add(station);
            }
            return found.OrderBy(station => station == target ? -1f : (station.transform.position - target.transform.position).sqrMagnitude).ToList();
        }
        // A base's whole stock for the loading a hover previews. Ordinary loading takes any carried copy and the chests around
        // this fermenter; filling skips protected copies and draws on the chests around every fermenter it may use.
        private static int Stock(Fermenter target, Player player, string prefab, bool bulk, float radius)
        {
            var stations = bulk ? Nearby(target, radius).Where(station => WardAccess.Allowed(station, player.GetPlayerID())) : new[] { target };
            return BulkProcessing.BagStock(player, prefab, bulk) +
                BulkProcessing.ChestStock(stations.Where(station => Allowed(station, prefab) && BulkProcessing.CanPull(station, prefab)), prefab, bulk);
        }
        private static Supply FirstSupply(IEnumerable<Fermenter> stations, Player player, bool bulk)
        {
            foreach (var station in stations)
                if (Unavailable(station, player.GetPlayerID()) == null && Select(station, player, bulk) is Supply supply) return supply;
            return null;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Fermenter), nameof(Fermenter.GetHoverText))]
        private static void Preview(Fermenter __instance, ref string __result)
        {
            var player = Player.m_localPlayer;
            var view = __instance.GetComponent<ZNetView>();
            if (!player || !view || !view.IsValid()) return;
            var localization = Localization.instance;
            bool busy = running;
            string activity = tapping ? "Tapping fermenters" : "Loading fermenters";
            if (Ready(__instance))
            {
                // The native ready line and its tap control stay as they are.
                if (!BulkPickup.TryGetHarvestRadius(out float tapRadius, true)) return;
                int ready = Tappable(__instance, player, tapRadius).Count;
                if (busy || ready > 0) __result += BulkPickup.Hint(busy ? activity : $"Tap nearby fermenters ({ready} ready)");
                return;
            }
            if (Content(__instance) == 0 && WardAccess.Allowed(__instance, player.GetPlayerID()))
            {
                string reason = Unavailable(__instance, player.GetPlayerID());
                var supply = reason == null ? Select(__instance, player, false) : null;
                string action = busy ? activity : supply == null ? localization.Localize(reason ?? "$msg_noprocessableitems") :
                    "Add " + localization.Localize(supply.Item.m_shared.m_name) + " (" + Stock(__instance, player, supply.Item.m_dropPrefab.name, false, 0f) + ")";
                // The native cover cache can be ten seconds old. Keep the empty heading's warning consistent with the preview without resetting a timer.
                int headingEnd = __result.IndexOf('\n');
                if (headingEnd >= 0 && __result.StartsWith(localization.Localize(__instance.m_name), StringComparison.Ordinal))
                    __result = localization.Localize(__instance.m_name + " ( $piece_container_empty" + (reason == null ? "" : ", " + reason) + " )") + __result.Substring(headingEnd);
                string original = localization.Localize("$piece_fermenter_add");
                int start = __result.LastIndexOf(original, StringComparison.Ordinal);
                if (start >= 0) __result = __result.Remove(start, original.Length).Insert(start, action);
            }
            if (BulkPickup.TryGetHarvestRadius(out float radius, true))
            {
                var supply = FirstSupply(Nearby(__instance, radius), player, true);
                __result += BulkPickup.Hint(busy ? activity : supply == null ? "No nearby fermenter can accept an available base" :
                    "Fill nearby fermenters with " + localization.Localize(supply.Item.m_shared.m_name) + " (" + Stock(__instance, player, supply.Item.m_dropPrefab.name, true, radius) + ")");
            }
        }

        [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyPatch(typeof(Fermenter), nameof(Fermenter.Interact))]
        private static bool Interact(Fermenter __instance, Humanoid user, bool hold, ref bool __result)
        {
            if (user != Player.m_localPlayer) return true;
            if (Ready(__instance)) return TapNearby(__instance, (Player)user, hold, ref __result);
            __result = false;
            if (hold || running) return false;
            if (Content(__instance) == 0) UpdateCover(__instance, 0f, true);
            bool bulk = BulkPickup.TryGetHarvestRadius(out float radius);
            if (!bulk && Content(__instance) != 0) return true;
            var player = (Player)user;
            if (!bulk && !WardAccess.Allowed(__instance, player.GetPlayerID())) { player.Message(MessageHud.MessageType.Center, "$piece_noaccess"); return false; }
            var stations = bulk ? Nearby(__instance, radius) : new List<Fermenter> { __instance };
            var supply = FirstSupply(stations, player, bulk);
            if (supply == null)
            {
                player.Message(MessageHud.MessageType.Center, bulk ? "No nearby fermenter can accept an available base" : Unavailable(__instance, player.GetPlayerID()) ?? "$msg_noprocessableitems");
                return false;
            }
            // The native finder can charge a chest itself, so handled interactions never call that finder or UseItem.
            __result = true;
            player.StartCoroutine(Fill(stations, player, bulk, supply.Item.m_dropPrefab.name));
            return false;
        }

        private static IEnumerator Fill(List<Fermenter> stations, Player player, bool bulk, string prefab)
        {
            running = true;
            int generation = playerGeneration;
            try
            {
                foreach (var station in stations)
                {
                    if (!player || player != Player.m_localPlayer || player.IsDead()) yield break;
                    if (Unavailable(station, player.GetPlayerID()) != null || !Allowed(station, prefab)) continue;
                    UpdateCover(station, 0f, true);
                    var source = Select(station, player, bulk, prefab);
                    if (source == null) continue;
                    var view = station.GetComponent<ZNetView>();
                    if (view.GetZDO().GetOwner() == 0L) view.ClaimOwnership();
                    // Another owner gives a new barrel its identity shortly; until then its payment could not be settled later.
                    long barrel = Identity(view);
                    if (barrel == 0L) continue;
                    var paid = source.Item.Clone(); paid.m_stack = 1; paid.m_gridPos = source.Item.m_gridPos;
                    if (!source.Inventory.RemoveOneItem(source.Item)) continue;
                    if (source.Chest != null) BulkProcessing.SaveChest(source.Chest);
                    var payment = new Payment { Barrel = barrel, Transaction = NewIdentity(), World = ZNet.instance.GetWorldUID(), Prefab = prefab, Cheated = paid.m_cheated, Item = paid };
                    Record(player, payment);
                    var placement = new Placement { Source = source, Paid = paid, ReturnPosition = player.transform.position + Vector3.up };
                    Send(view, payment, placement);
                    float deadline = Time.realtimeSinceStartup + 5f;
                    while (generation == playerGeneration && !placement.Accepted.HasValue && Time.realtimeSinceStartup < deadline) yield return null;
                    if (generation != playerGeneration) yield break;
                    if (!placement.Accepted.HasValue)
                    {
                        placement.Source = null;
                        player.Message(MessageHud.MessageType.Center, "The fermenter did not respond.");
                        yield break;
                    }
                    yield return null;
                }
            }
            finally { if (generation == playerGeneration) running = false; }
        }

        // The ready fermenters in range that this player may tap, target first.
        private static List<Fermenter> Tappable(Fermenter target, Player player, float radius) =>
            Nearby(target, radius).Where(station => Ready(station) && WardAccess.Allowed(station, player.GetPlayerID())).ToList();

        // With the bulk modifier, a ready target taps every tappable fermenter in range and loads nothing. Without the
        // modifier, or when none of them can be tapped, the native tap or refusal runs.
        private static bool TapNearby(Fermenter target, Player player, bool hold, ref bool result)
        {
            if (hold || !BulkPickup.TryGetHarvestRadius(out float radius)) return true;
            result = false;
            if (running) return false;
            var stations = Tappable(target, player, radius);
            if (stations.Count == 0) return true;
            result = true;
            player.StartCoroutine(Tap(stations, player));
            return false;
        }

        // Each tap goes to that barrel's owner, which taps only a ready barrel, so its mead drops there natively. The batch
        // waits until the barrel reads empty before tapping the next one.
        private static IEnumerator Tap(List<Fermenter> stations, Player player)
        {
            running = tapping = true;
            int generation = playerGeneration;
            try
            {
                foreach (var station in stations)
                {
                    if (!player || player != Player.m_localPlayer || player.IsDead()) yield break;
                    var view = station ? station.GetComponent<ZNetView>() : null;
                    if (!view || !view.IsValid() || !Ready(station) || !WardAccess.Allowed(station, player.GetPlayerID())) continue;
                    if (view.GetZDO().GetOwner() == 0L) view.ClaimOwnership();
                    view.InvokeRPC("RPC_Tap");
                    float deadline = Time.realtimeSinceStartup + 5f;
                    while (generation == playerGeneration && station && view.IsValid() && Ready(station) && Time.realtimeSinceStartup < deadline) yield return null;
                    if (generation != playerGeneration) yield break;
                    if (station && view.IsValid() && Ready(station))
                    {
                        player.Message(MessageHud.MessageType.Center, "The fermenter did not respond.");
                        yield break;
                    }
                    yield return null;
                }
            }
            finally { if (generation == playerGeneration) running = tapping = false; }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Fermenter), "Awake")]
        private static void Register(Fermenter __instance, ZNetView ___m_nview)
        {
            if (!___m_nview || !___m_nview.IsValid()) return;
            ___m_nview.Register<long, string, bool>(PlaceRpc, (sender, transaction, prefab, cheated) => Place(__instance, ___m_nview, sender, transaction, prefab, cheated));
            ___m_nview.Register<long, bool>(ReceiptRpc, (sender, transaction, accepted) => Receipt(___m_nview, sender, transaction, accepted));
        }

        // The barrel's own identity, given once by its owner, since the network ID changes whenever the world loads.
        private static long Identity(ZNetView view)
        {
            var data = view.GetZDO();
            long identity = data.GetLong(IdentityKey);
            if (identity == 0L && view.IsOwner()) data.Set(IdentityKey, identity = NewIdentity());
            return identity;
        }
        private static long NewIdentity()
        {
            long identity;
            do identity = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0); while (identity == 0L);
            return identity;
        }

        private static IEnumerable<KeyValuePair<string, string>> Payments(Player player, string prefix = PaymentKey) =>
            player.m_customData.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        private static void Record(Player player, Payment payment)
        {
            var holder = new Inventory("", null, 1, 1);
            holder.AddItem(payment.Item.Clone(), new Vector2i(0, 0));
            var item = new ZPackage(); holder.Save(item);
            var package = new ZPackage();
            package.Write(payment.World); package.Write(payment.Prefab); package.Write(payment.Cheated); package.Write(payment.Rejected); package.Write(item);
            player.m_customData[PaymentKey + payment.Barrel.ToString("x16") + "." + payment.Transaction.ToString("x16")] = package.GetBase64();
        }
        private static Payment Read(KeyValuePair<string, string> entry)
        {
            var identities = entry.Key.Substring(PaymentKey.Length).Split('.');
            var package = new ZPackage(entry.Value);
            var payment = new Payment
            {
                Barrel = long.Parse(identities[0], NumberStyles.HexNumber), Transaction = long.Parse(identities[1], NumberStyles.HexNumber),
                World = package.ReadLong(), Prefab = package.ReadString(), Cheated = package.ReadBool(), Rejected = package.ReadBool()
            };
            var holder = new Inventory("", null, 1, 1);
            holder.Load(package.ReadPackage());
            payment.Item = holder.GetAllItems().FirstOrDefault();
            return payment;
        }

        private static void Send(ZNetView view, Payment payment, Placement placement)
        {
            placement.Sent = Time.realtimeSinceStartup;
            Placements[payment.Transaction] = placement;
            view.InvokeRPC(view.GetZDO().GetOwner(), PlaceRpc, payment.Transaction, payment.Prefab, payment.Cheated);
        }

        // Only the barrel's owner decides, once per transaction, and saves the outcome with the barrel; a repeated request
        // gets that outcome again.
        private static void Place(Fermenter station, ZNetView view, long sender, long transaction, string prefab, bool cheated)
        {
            if (!view.IsValid() || !view.IsOwner()) return;
            var receipts = Receipts(view.GetZDO());
            int known = receipts.FindIndex(receipt => receipt.Transaction == transaction);
            bool accepted;
            if (known >= 0) accepted = receipts[known].Accepted;
            else
            {
                accepted = Unavailable(station, SenderPlayerId(sender)) == null && Allowed(station, prefab);
                if (accepted)
                {
                    UpdateCover(station, 0f, true);
                    AddItem(station, sender, prefab.GetStableHashCode(), cheated);
                    accepted = Content(station) == prefab.GetStableHashCode();
                }
                receipts.Add((transaction, accepted));
                if (receipts.Count > ReceiptLimit) receipts.RemoveRange(0, receipts.Count - ReceiptLimit);
                var package = new ZPackage();
                package.Write(receipts.Count);
                foreach (var receipt in receipts) { package.Write(receipt.Transaction); package.Write(receipt.Accepted); }
                view.GetZDO().Set(ReceiptsKey, package.GetArray());
            }
            view.InvokeRPC(sender, ReceiptRpc, transaction, accepted);
        }
        // The latest outcomes saved with the barrel, oldest first.
        private static List<(long Transaction, bool Accepted)> Receipts(ZDO data)
        {
            var receipts = new List<(long, bool)>();
            byte[] bytes = data.GetByteArray(ReceiptsKey);
            if (bytes == null) return receipts;
            var package = new ZPackage(bytes);
            for (int count = package.ReadInt(); count > 0; count--) receipts.Add((package.ReadLong(), package.ReadBool()));
            return receipts;
        }
        private static long SenderPlayerId(long sender)
        {
            var player = Player.GetAllPlayers().FirstOrDefault(candidate => candidate.GetOwner() == sender);
            if (player) return player.GetPlayerID();
            // Dedicated servers need not instantiate avatars. Use the authenticated peer's character, never an ID supplied in the request.
            var peer = ZNet.instance.GetPeer(sender);
            if (peer == null || !peer.IsReady()) return 0L;
            var character = ZDOMan.instance.GetZDO(peer.m_characterID);
            return character != null && character.GetPrefab() == "Player".GetStableHashCode() && character.GetOwner() == sender
                ? character.GetLong(ZDOVars.s_playerID) : 0L;
        }
        // A reply counts only from the barrel's owner; otherwise the payment waits for the owner's saved outcome.
        private static void Receipt(ZNetView view, long sender, long transaction, bool accepted)
        {
            if (view.IsValid() && view.GetZDO().GetOwner() == sender) Settle(transaction, accepted);
        }
        // An outcome for one of this character's payments. A repeated outcome finds its payment already settled.
        private static void Settle(long transaction, bool accepted)
        {
            var player = Player.m_localPlayer;
            if (!player) return;
            string suffix = "." + transaction.ToString("x16");
            var entry = Payments(player).FirstOrDefault(candidate => candidate.Key.EndsWith(suffix, StringComparison.Ordinal));
            if (entry.Key == null) return;
            Placements.TryGetValue(transaction, out var placement);
            Placements.Remove(transaction);
            if (placement != null) placement.Accepted = accepted;
            if (accepted) player.m_customData.Remove(entry.Key);
            else Refund(player, entry.Key, Read(entry), placement);
        }
        // A dead character's bag went to its tombstone, so a refusal then is saved and refunded to the living character.
        private static void Refund(Player player, string key, Payment payment, Placement placement)
        {
            var item = placement?.Paid ?? payment.Item;
            if (player.IsDead())
            {
                if (!payment.Rejected) { payment.Rejected = true; Record(player, payment); }
                return;
            }
            player.m_customData.Remove(key);
            var source = placement?.Source;
            if (source != null)
            {
                var inventory = source.Inventory;
                bool returned = inventory.GetItemAt(item.m_gridPos.x, item.m_gridPos.y) == null && inventory.AddItem(item, item.m_gridPos);
                if (!returned) returned = inventory.AddItem(item);
                if (returned) { if (source.Chest != null) BulkProcessing.SaveChest(source.Chest); }
                else ItemDrop.DropItem(item, 1, placement.ReturnPosition, Quaternion.identity);
                return;
            }
            // The batch has moved on and its chest may have unloaded, so a late refund goes to the player.
            if (!player.GetInventory().AddItem(item)) ItemDrop.DropItem(item, 1, player.transform.position + Vector3.up, Quaternion.identity);
        }

        // Settles this character's payments for a loaded barrel from the outcome saved with it, or asks its owner again
        // every ten seconds while it has none.
        [HarmonyPostfix, HarmonyPatch(typeof(Fermenter), "SlowUpdate")]
        private static void Reconcile(ZNetView ___m_nview)
        {
            if (!___m_nview || !___m_nview.IsValid()) return;
            long barrel = Identity(___m_nview);
            var player = Player.m_localPlayer;
            if (barrel == 0L || !player) return;
            foreach (var entry in Payments(player, PaymentKey + barrel.ToString("x16") + "."))
            {
                var payment = Read(entry);
                if (payment.World != ZNet.instance.GetWorldUID()) continue;
                if (payment.Rejected) { Refund(player, entry.Key, payment, null); continue; }
                var receipt = Receipts(___m_nview.GetZDO()).FirstOrDefault(candidate => candidate.Transaction == payment.Transaction);
                if (receipt.Transaction != 0L) { Settle(payment.Transaction, receipt.Accepted); continue; }
                if (Placements.TryGetValue(payment.Transaction, out var placement) && Time.realtimeSinceStartup - placement.Sent < 10f) continue;
                if (___m_nview.GetZDO().GetOwner() == 0L) ___m_nview.ClaimOwnership();
                Send(___m_nview, payment, placement ?? new Placement { ReturnPosition = player.transform.position + Vector3.up });
            }
        }
        [HarmonyPostfix, HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static void RefundSaved(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            foreach (var entry in Payments(__instance))
            {
                var payment = Read(entry);
                if (payment.Rejected) Refund(__instance, entry.Key, payment, null);
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.SetLocalPlayer))]
        private static void NewPlayer(Player __instance) { if (Player.m_localPlayer != __instance) Shutdown(); }
        [HarmonyPrefix, HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        private static void Shutdown() { playerGeneration++; running = tapping = false; Placements.Clear(); }
    }
}
