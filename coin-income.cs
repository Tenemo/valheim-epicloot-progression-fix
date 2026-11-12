using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using EpicLoot.Adventure;
using EpicLoot.Adventure.Feature;
using EpicLoot.MagicItemEffects.Shards;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class CoinIncome
    {
        internal const string PendingKey = Plugin.Id + ".coin-income.uncredited";
        private static readonly AccessTools.FieldRef<Player, bool> Loading = AccessTools.FieldRefAccess<Player, bool>("m_isLoading");
        private static readonly AccessTools.FieldRef<ItemDrop, ZNetView> View = AccessTools.FieldRefAccess<ItemDrop, ZNetView>("m_nview");
        private static readonly Action<Inventory, bool, bool> Changed = AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(AccessTools.Method(typeof(Inventory), "Changed"));
        [ThreadStatic] private static HashSet<Inventory> mutations;
        [ThreadStatic] private static Scope scope;
        [ThreadStatic] private static StackScope stacking;
        private static readonly ConditionalWeakTable<Inventory, Container> Containers = new ConditionalWeakTable<Inventory, Container>();
        internal static bool Enabled => CoinIncomeChannel.Enabled && ZNet.instance != null;
        internal static bool IsCurrency(ItemDrop.ItemData item) => item?.m_dropPrefab != null && CoinIncomeRules.Currency(item.m_dropPrefab.name);
        internal static int Count(Inventory inventory, string currency) => inventory.GetAllItems().Where(item => IsCurrency(item) && item.m_dropPrefab.name == currency).Sum(item => item.m_stack);
        internal static int Pending(ItemDrop.ItemData item) => CoinIncomeRules.Pending(item.m_stack, item.m_pickedUp,
            item.m_customData.TryGetValue(PendingKey, out string saved) ? saved : null);
        internal static bool SetPending(ItemDrop.ItemData item, int amount)
        {
            string value = Math.Max(0, Math.Min(item.m_stack, amount)).ToString(CultureInfo.InvariantCulture);
            if (item.m_customData.TryGetValue(PendingKey, out string previous) && previous == value) return false;
            item.m_customData[PendingKey] = value;
            return true;
        }
        private static bool Local(Inventory inventory) => CoinIncomeChannel.Active && Player.m_localPlayer.GetInventory() == inventory && !Loading(Player.m_localPlayer);

        internal sealed class Quantity { internal ItemDrop.ItemData Item; internal int Stack, Pending; }
        internal static List<Quantity> Snapshot(Inventory inventory) => inventory.GetAllItems().Where(IsCurrency)
            .Select(item => new Quantity { Item = item, Stack = item.m_stack, Pending = Pending(item) }).ToList();
        internal sealed class Mutation
        {
            internal Inventory Inventory;
            internal List<Quantity> Before;
            internal ItemDrop.ItemData Incoming;
            internal int IncomingPending;
            internal string Currency;
            internal bool Cheated;
        }
        internal static Mutation Begin(Inventory inventory, ItemDrop.ItemData incoming)
        {
            if (!Enabled || (incoming != null && !IsCurrency(incoming))) return null;
            if (mutations == null) mutations = new HashSet<Inventory>();
            if (!mutations.Add(inventory)) return null;
            try
            {
                return new Mutation { Inventory = inventory, Before = Snapshot(inventory), Incoming = incoming,
                    IncomingPending = incoming == null ? 0 : scope?.LegacyStorage == true && !incoming.m_customData.ContainsKey(PendingKey) ? 0 : Pending(incoming), Currency = incoming?.m_dropPrefab.name,
                    Cheated = incoming?.m_cheated ?? false };
            }
            catch (Exception error) { mutations.Remove(inventory); CoinIncomeChannel.Error(error); return null; }
        }
        internal static void End(Mutation change)
        {
            if (change == null) return;
            try
            {
                var after = change.Inventory.GetAllItems().Where(IsCurrency).ToList();
                var old = change.Before.ToDictionary(item => item.Item);
                int moved = change.Currency == null ? 0 : after.Where(item => item.m_dropPrefab.name == change.Currency)
                    .Sum(item => Math.Max(0, item.m_stack - (old.TryGetValue(item, out var previous) ? previous.Stack : 0)));
                int fresh = Math.Min(moved, change.IncomingPending), remaining = fresh;
                bool local = Local(change.Inventory), direct = scope != null && scope.Income, refund = scope?.Source == "refund";
                bool changed = false;
                foreach (var item in after)
                {
                    int previousStack = old.TryGetValue(item, out var previous) ? previous.Stack : 0;
                    int previousPending = previous?.Pending ?? 0;
                    int added = Math.Max(0, item.m_stack - previousStack), removed = Math.Max(0, previousStack - item.m_stack);
                    int addedFresh = item.m_dropPrefab.name == change.Currency ? Math.Min(remaining, added) : 0;
                    remaining -= addedFresh;
                    int pending = Math.Max(0, previousPending - removed) + addedFresh;
                    if (local && added > 0) pending -= addedFresh;
                    changed |= SetPending(item, pending);
                }
                if (change.Incoming != null && !after.Contains(change.Incoming))
                    SetPending(change.Incoming, Math.Max(0, change.IncomingPending - fresh));
                if (changed) Changed(change.Inventory, false, false);
                if (!local) return;
                foreach (string currency in new[] { "Coins", "GoldBountyToken" })
                {
                    int beforeCount = change.Before.Where(item => item.Item.m_dropPrefab.name == currency).Sum(item => item.Stack);
                    int delta = Count(change.Inventory, currency) - beforeCount;
                    if (delta > 0)
                    {
                        if (change.Cheated) Record(currency, delta, "unclassified", "admin item");
                        else if (refund) Record(currency, delta, "refund", "refund");
                        else if (direct) Record(currency, delta, "income", scope.Source);
                        else
                        {
                            int earned = currency == change.Currency ? Math.Min(fresh, delta) : 0;
                            Record(currency, earned, "income", "loot");
                            Record(currency, delta - earned, "transfer-in", "transfer");
                        }
                    }
                    else if (delta < 0 && scope?.SuppressDebits != true)
                        Record(currency, -delta, scope?.Source == "transfer" ? "transfer-out" : "spend", scope?.Source ?? "spend");
                }
            }
            catch (Exception error) { CoinIncomeChannel.Error(error); }
            finally { mutations.Remove(change.Inventory); }
        }
        private static void Record(string currency, int amount, string kind, string source)
        {
            if (amount <= 0) return;
            if (scope != null && (kind == "income" || kind == "refund"))
                scope.Received[currency] = (scope.Received.TryGetValue(currency, out int previous) ? previous : 0) + amount;
            CoinIncomeChannel.Record(currency, amount, kind, source, scope?.Reference);
        }

        internal sealed class Scope : IDisposable
        {
            internal string Source, Reference;
            internal bool Income, SuppressDebits, LegacyStorage;
            internal int? DroppedFresh;
            internal Scope Previous;
            internal List<ItemDrop> Drops = new List<ItemDrop>();
            internal Dictionary<string, int> Expected = new Dictionary<string, int>();
            internal Dictionary<string, int> Received = new Dictionary<string, int>();
            public void Dispose()
            {
                try
                {
                    foreach (var drop in Drops.Where(item => item && IsCurrency(item.m_itemData) && View(item) != null && View(item).IsValid()))
                    {
                        if (Income || Source == "refund")
                        {
                            SetPending(drop.m_itemData, 0);
                            if (CoinIncomeChannel.Active)
                                Record(drop.m_itemData.m_dropPrefab.name, drop.m_itemData.m_stack, Income ? "income" : "refund", Source);
                        }
                        else if (DroppedFresh.HasValue) SetPending(drop.m_itemData, DroppedFresh.Value);
                        ItemDrop.SaveToZDO(drop.m_itemData, View(drop).GetZDO());
                    }
                    foreach (var expected in Expected)
                    {
                        int missing = expected.Value - (Received.TryGetValue(expected.Key, out int actual) ? actual : 0);
                        Record(expected.Key, missing, "shortfall", "undelivered " + Source);
                    }
                }
                catch (Exception error) { CoinIncomeChannel.Error(error); }
                finally { scope = Previous; }
            }
        }
        internal static Scope Enter(string source, bool income, string reference = null)
        {
            if (!Enabled) return null;
            var entered = new Scope { Source = source, Income = income, Reference = reference, Previous = scope };
            scope = entered;
            return entered;
        }
        [HarmonyPatch(typeof(Container), "Awake"), HarmonyPostfix]
        private static void ContainerCreated(Container __instance)
        {
            var inventory = __instance.GetInventory();
            if (inventory != null) Containers.Add(inventory, __instance);
        }
        [HarmonyPatch]
        private static class StoredCurrencyLoad
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(Inventory).GetMethods(AccessTools.all)
                .Where(method => method.Name == "Load");
            private static void Prefix(Inventory __instance, out Scope __state)
            {
                __state = null;
                if (!Enabled || !Containers.TryGetValue(__instance, out var container) || !container) return;
                var piece = container.GetComponent<Piece>();
                if (!piece && container.m_rootObjectOverride) piece = container.m_rootObjectOverride.GetComponent<Piece>();
                if (!container.GetComponent<TombStone>() && piece?.IsPlacedByPlayer() != true) return;
                __state = Enter("opening storage", false);
                __state.LegacyStorage = true;
            }
            private static Exception Finalizer(Exception __exception, Scope __state) { __state?.Dispose(); return __exception; }
        }
        [HarmonyPatch]
        private static class CurrencyAddition
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(Inventory).GetMethods(AccessTools.all)
                .Where(method => method.Name == "AddItem" && method.GetParameters().Length > 0 && method.GetParameters()[0].ParameterType == typeof(ItemDrop.ItemData));
            private static void Prefix(Inventory __instance, ItemDrop.ItemData __0, out Mutation __state) => __state = Begin(__instance, __0);
            private static Exception Finalizer(Exception __exception, Mutation __state) { End(__state); return __exception; }
        }
        [HarmonyPatch]
        private static class CurrencyRemoval
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(Inventory).GetMethods(AccessTools.all)
                .Where(method => method.Name == "RemoveItem" || method.Name == "RemoveOneItem" || method.Name == "RemoveAll");
            private static void Prefix(Inventory __instance, out Mutation __state) => __state = Begin(__instance, null);
            private static Exception Finalizer(Exception __exception, Mutation __state) { End(__state); return __exception; }
        }
        [HarmonyPatch]
        private static class DirectCurrencyCreation
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(Inventory).GetMethods(AccessTools.all)
                .Where(method => method.Name == "AddItem" && method.IsPublic && method.GetParameters().Length > 0 &&
                    (method.GetParameters()[0].ParameterType == typeof(string) || method.GetParameters()[0].ParameterType == typeof(GameObject)));
            private static void Prefix(out Scope __state) => __state = scope == null ? Enter("other", true) : null;
            private static Exception Finalizer(Exception __exception, Scope __state) { __state?.Dispose(); return __exception; }
        }
        [HarmonyPatch(typeof(StoreGui), "SellItem")]
        private static class Sales
        {
            private static void Prefix(out Scope __state) => __state = Enter("sale", true);
            private static Exception Finalizer(Exception __exception, Scope __state) { __state?.Dispose(); return __exception; }
        }
        [HarmonyPatch(typeof(StoreGui), "BuySelectedItem")]
        private static class Purchases
        {
            private static void Prefix(out Scope __state) => __state = Enter("purchase", false);
            private static Exception Finalizer(Exception __exception, Scope __state) { __state?.Dispose(); return __exception; }
        }
        [HarmonyPatch(typeof(BountiesAdventureFeature), nameof(BountiesAdventureFeature.ClaimBountyReward))]
        private static class Bounties
        {
            private static void Prefix(BountyInfo bountyInfo, out Scope __state)
            {
                __state = bountyInfo?.State == BountyState.Complete ? Enter("bounty", true, bountyInfo.ID) : null;
                if (__state != null)
                {
                    __state.Expected["Coins"] = bountyInfo.RewardCoins;
                    __state.Expected["GoldBountyToken"] = bountyInfo.RewardGold;
                }
            }
            private static Exception Finalizer(Exception __exception, Scope __state) { __state?.Dispose(); return __exception; }
        }
        [HarmonyPatch]
        private static class Refunds
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(Wager).Assembly.GetType("EpicLoot.General.CoinPurse", true), "Refund");
            private static void Prefix(int amount, out Scope __state)
            {
                __state = Enter("refund", false);
                if (__state != null) __state.Expected["Coins"] = amount;
            }
            private static Exception Finalizer(Exception __exception, Scope __state) { __state?.Dispose(); return __exception; }
        }
        // A table upgrade that was refused or got no reply returns its payment here, as a copy of the cost item.
        [HarmonyPatch(typeof(UpgradeTableUI), "Refund")]
        private static class TableUpgradeRefunds
        {
            private static void Prefix(out Scope __state) => __state = Enter("refund", false);
            private static Exception Finalizer(Exception __exception, Scope __state) { __state?.Dispose(); return __exception; }
        }
        [HarmonyPatch]
        private static class CurrencySpending
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(Wager).Assembly.GetType("EpicLoot.General.CoinPurse", true), "Spend");
            private static void Prefix(out Scope __state) => __state = scope == null ? Enter("rune", false) : null;
            private static Exception Finalizer(Exception __exception, Scope __state) { __state?.Dispose(); return __exception; }
        }
        internal sealed class Transfer { internal Inventory From; internal Dictionary<string, int> Before; internal Scope Scope; internal bool RecordDebit; }
        [HarmonyPatch]
        private static class Transfers
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(Inventory).GetMethods(AccessTools.all)
                .Where(method => method.Name == "MoveAll" || method.Name == "MoveItemToThis" || method.Name == "StackAll" || method.Name == "MoveInventoryToGrave");
            private static void Prefix(Inventory __0, out Transfer __state)
            {
                bool outer = scope?.Source != "transfer";
                __state = Enabled ? new Transfer { From = __0, Scope = Enter("transfer", false), RecordDebit = outer,
                    Before = new[] { "Coins", "GoldBountyToken" }.ToDictionary(currency => currency, currency => Count(__0, currency)) } : null;
                if (__state != null) __state.Scope.SuppressDebits = true;
            }
            private static Exception Finalizer(Exception __exception, Transfer __state)
            {
                if (__state == null) return __exception;
                try
                {
                    if (__state.RecordDebit && Local(__state.From))
                        foreach (var item in __state.Before) Record(item.Key, item.Value - Count(__state.From, item.Key), "transfer-out", "transfer");
                }
                catch (Exception error) { CoinIncomeChannel.Error(error); }
                finally { __state.Scope?.Dispose(); }
                return __exception;
            }
        }
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DropItem))]
        private static class Dropping
        {
            private static void Prefix(ItemDrop.ItemData item, int amount, out Scope __state)
            {
                __state = Enter("transfer", false);
                if (__state != null && IsCurrency(item)) __state.DroppedFresh = Math.Min(Pending(item), Math.Max(0, amount));
            }
            private static Exception Finalizer(Exception __exception, Scope __state) { __state?.Dispose(); return __exception; }
        }
        [HarmonyPatch(typeof(ItemDrop), "Awake"), HarmonyPostfix]
        private static void Created(ItemDrop __instance) { if (Enabled && scope != null && IsCurrency(__instance.m_itemData)) scope.Drops.Add(__instance); }
        [HarmonyPatch(typeof(Player), nameof(Player.Load)), HarmonyPostfix]
        private static void CharacterLoaded(Player __instance)
        {
            if (!Enabled) return;
            foreach (var item in __instance.GetInventory().GetAllItems().Where(IsCurrency)) SetPending(item, 0);
        }
        internal sealed class StackScope { internal ItemDrop Target; internal int Pending; internal StackScope Previous; }
        [HarmonyPatch(typeof(ItemDrop), "AutoStackItems")]
        private static class LooseStacking
        {
            // EpicLoot replaces this method and merges the piles in its own prefix, so the scope opens before it.
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ItemDrop __instance, out StackScope __state)
            {
                __state = null;
                if (!Enabled || !IsCurrency(__instance.m_itemData)) return;
                try
                {
                    __state = new StackScope { Target = __instance, Pending = Pending(__instance.m_itemData), Previous = stacking };
                    stacking = __state;
                }
                catch (Exception error) { CoinIncomeChannel.Error(error); }
            }
            private static Exception Finalizer(Exception __exception, StackScope __state)
            {
                if (__state == null) return __exception;
                try
                {
                    if (__state.Target && View(__state.Target) != null && View(__state.Target).IsValid())
                    {
                        SetPending(__state.Target.m_itemData, __state.Pending);
                        ItemDrop.SaveToZDO(__state.Target.m_itemData, View(__state.Target).GetZDO());
                    }
                }
                catch (Exception error) { CoinIncomeChannel.Error(error); }
                finally { stacking = __state.Previous; }
                return __exception;
            }
        }
        [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.Destroy)), HarmonyPrefix]
        private static void MergedPile(ZNetView __instance)
        {
            if (stacking == null) return;
            var drop = __instance.GetComponent<ItemDrop>();
            if (drop && drop != stacking.Target && IsCurrency(drop.m_itemData) && drop.m_itemData.m_dropPrefab == stacking.Target.m_itemData.m_dropPrefab)
                stacking.Pending += Pending(drop.m_itemData);
        }
    }
}
