using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using System.Runtime.CompilerServices;
using UnityEngine;
using BepInEx.Logging;
using EpicLoot;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot 0.14.13 rolls a location chest's loot once, for whichever player's game first needs it: an inventory read,
    // hovering over the chest, or pressing Use before the game checks whether the chest opens. The loot follows that
    // player's crafting history and stays in the chest for everyone, so a beginner glancing at a fuling village chest
    // left club-tier gear in it for every later player. The loot is now rolled for the player whose open is granted,
    // once their crafting history has loaded, and the chest's owner adds it to the chest exactly once, so the chest's
    // other viewers, later openers and ownership changes never roll it again. The chest's own loot tables still cap its
    // equipment tier. Taking everything from a chest and destroying it collect it too, for the player who did so; on
    // the owner's game, another player's hit, a creature, fire or a collapse leaves the loot unrolled. Chests that rolled
    // before keep their contents.
    internal static class DeferredChestLoot
    {
        private const string HistoryRpc = Plugin.Id + ".chest-opener";
        private static readonly ConditionalWeakTable<Container, Dictionary<long, float>> Grants = new();
        private sealed class OpenRequest { internal long Peer; internal bool TakeAll; }
        private static readonly ConditionalWeakTable<Container, OpenRequest> Requests = new();
        private static readonly Func<Container, bool> Load = AccessTools.MethodDelegate<Func<Container, bool>>(AccessTools.Method(typeof(Container), "Load"));
        private static ManualLogSource logger;
        private static HitData destroyingHit;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            logger = log;
            // EpicLoot's rolls on an inventory read, hover text, pressing Use, destruction and take all.
            foreach (var (method, patch) in new[] {
                (nameof(Container.GetInventory), typeof(EpicLoot.Container_GetInventory_Patch)),
                (nameof(Container.GetHoverText), typeof(EpicLoot.Container_GetHoverText_Patch)),
                (nameof(Container.Interact), typeof(EpicLoot.Container_Interact_Patch)),
                ("OnDestroyed", typeof(EpicLoot.Container_OnDestroyed_Patch)),
                ("RPC_TakeAllResponse", typeof(EpicLoot.Container_RPC_TakeAllRespons_Patch)) })
            {
                var original = AccessTools.Method(typeof(Container), method);
                var roll = AccessTools.Method(patch, "Prefix");
                if (original == null || roll == null) throw new MissingMethodException("EpicLoot's deferred chest roll changed.");
                harmony.Unpatch(original, roll);
            }
            harmony.Patch(AccessTools.Method(typeof(ZNetView), nameof(ZNetView.InvokeRPC), new[] { typeof(string), typeof(object[]) }),
                prefix: Patch(nameof(Requesting)));
            harmony.Patch(AccessTools.Method(typeof(ZNetView), nameof(ZNetView.InvokeRPC), new[] { typeof(long), typeof(string), typeof(object[]) }),
                prefix: Patch(nameof(SendingResponse)));
            harmony.Patch(AccessTools.Method(typeof(Container), "Awake"), postfix: Patch(nameof(Register)));
            harmony.Patch(AccessTools.Method(typeof(Container), "RPC_OpenResponse"), prefix: Patch(nameof(Opened)));
            harmony.Patch(AccessTools.Method(typeof(Container), "RPC_TakeAllResponse"), prefix: Patch(nameof(TakingAll)));
            harmony.Patch(AccessTools.Method(typeof(Container), "OnDestroyed"), prefix: Patch(nameof(Destroying)));
            harmony.Patch(AccessTools.Method(typeof(WearNTear), "Destroy"), prefix: Patch(nameof(BeforeDestroy)),
                finalizer: Patch(nameof(AfterDestroy)));
        }

        private static HarmonyMethod Patch(string name) => new HarmonyMethod(typeof(DeferredChestLoot), name);

        private static void Register(Container __instance)
        {
            var view = GameFields.ContainerView(__instance);
            if (view != null && view.GetZDO() != null && PendingChestLoot.TryGetLootTables(__instance, out _, out _))
                view.Register<long, ZPackage>(HistoryRpc, (sender, playerId, package) => ReceiveHistory(__instance, sender, playerId, package));
        }

        private static void Requesting(ZNetView __instance, string __0)
        {
            if (__0 != "RPC_RequestOpen" && __0 != "RPC_RequestTakeAll") return;
            var chest = __instance.GetComponent<Container>();
            if (!chest || !Pending(chest, out var data)) return;
            var request = Requests.GetOrCreateValue(chest);
            request.Peer = data.GetOwner();
            request.TakeAll = __0 == "RPC_RequestTakeAll";
        }

        private static bool Requested(Container chest, long peer, bool takeAll)
        {
            if (!Requests.TryGetValue(chest, out var request) || request.Peer != peer || request.TakeAll != takeAll) return false;
            Requests.Remove(chest);
            return true;
        }

        // MultiUserChest can grant a second viewer access while keeping the first viewer as owner.
        private static void SendingResponse(ZNetView __instance, long __0, string __1, object[] __2)
        {
            if (__1 != "RPC_OpenResponse" || __2.Length != 1 || !(__2[0] is bool granted) || !granted || !__instance.IsOwner()) return;
            var chest = __instance.GetComponent<Container>();
            if (chest && Pending(chest, out _)) Grants.GetOrCreateValue(chest)[__0] = Time.realtimeSinceStartup;
        }

        private static void Opened(Container __instance, bool granted, long __0)
        {
            if (!Requested(__instance, __0, false) || !granted || !Player.m_localPlayer || !Pending(__instance, out _)) return;
            var view = GameFields.ContainerView(__instance);
            if (view.IsOwner()) Collect(__instance, "opened", TreasureProgression.Capture(Player.m_localPlayer.GetPlayerID()));
            else __instance.StartCoroutine(WaitForOwner(__instance, __0, Player.m_localPlayer));
        }

        private static IEnumerator WaitForOwner(Container chest, long grantingPeer, Player player)
        {
            var history = TreasureProgression.Capture(player.GetPlayerID());
            if (Eligibility.CraftingMode && history == null) yield break;
            var view = GameFields.ContainerView(chest);
            // A shared owner rolls from its own tables, using only the opener's eligibility snapshot.
            var package = new ZPackage(history?.Encode() ?? Array.Empty<byte>());
            view.InvokeRPC(grantingPeer, HistoryRpc, player.GetPlayerID(), package);
            float until = Time.realtimeSinceStartup + 10f;
            while (chest && view.IsValid() && player == Player.m_localPlayer && Pending(chest, out _) && Time.realtimeSinceStartup < until)
            {
                if (view.IsOwner()) { Collect(chest, "opened", history); yield break; }
                yield return null;
            }
        }

        private static void ReceiveHistory(Container chest, long sender, long playerId, ZPackage package)
        {
            var view = GameFields.ContainerView(chest);
            if (!view.IsOwner() || !Pending(chest, out _) || !Grants.TryGetValue(chest, out var grants) ||
                !grants.TryGetValue(sender, out float at) || Time.realtimeSinceStartup - at > 10f) return;
            grants.Remove(sender);
            var history = Eligibility.CraftingMode ? TreasureProgression.BuyerHistory.Decode(package.GetArray(), playerId) : null;
            Collect(chest, "opened", history);
        }

        private static void TakingAll(Container __instance, long __0, bool granted)
        {
            if (!Requested(__instance, __0, true) || !granted || Player.m_localPlayer == null || !Pending(__instance, out _)) return;
            // The game claims the chest before moving everything; claiming it first adds the loot here, before the move.
            GameFields.ContainerView(__instance).ClaimOwnership();
            Collect(__instance, "took everything from", TreasureProgression.Capture(Player.m_localPlayer.GetPlayerID()));
        }

        private static void BeforeDestroy(HitData hitData, out HitData __state)
        {
            __state = destroyingHit;
            destroyingHit = hitData;
        }

        private static void AfterDestroy(HitData __state) => destroyingHit = __state;

        // A chest is destroyed on its owner's game, which knows only its own character's crafting history.
        private static void Destroying(Container __instance)
        {
            var player = Player.m_localPlayer;
            if (player != null && destroyingHit != null && destroyingHit.GetAttacker() == player) Collect(__instance, "destroyed", TreasureProgression.Capture(player.GetPlayerID()));
        }

        private static bool Pending(Container chest, out ZDO data)
        {
            var view = GameFields.ContainerView(chest);
            data = view != null && view.IsValid() ? view.GetZDO() : null;
            return data != null && data.GetBool(PendingChestLoot.PendingKey);
        }

        private static void Collect(Container chest, string action, TreasureProgression.BuyerHistory history)
        {
            var view = GameFields.ContainerView(chest);
            if (!view.IsOwner() || !Pending(chest, out var data) || !PendingChestLoot.TryGetLootTables(chest, out string name, out var tables)) return;
            if (Eligibility.CraftingMode && history == null) return;
            var previous = TreasureProgression.Current;
            try
            {
                TreasureProgression.Current = history;
                List<ItemDrop.ItemData> items;
                // A roll that fails leaves the chest unrolled, and it still opens and empties as usual.
                try { items = LootRoller.RollLootTable(tables, 1, chest.GetComponent<Piece>().name, chest.transform.position); }
                catch (Exception error)
                {
                    logger.LogError($"{name} at {chest.transform.position:F0} stays unrolled; its roll when {action} failed: {error}");
                    return;
                }
                Load(chest);
                ContainerCapacity.AddItems(chest, items);
                data.Set(PendingChestLoot.PendingKey, false);
                Grants.Remove(chest);
                logger.LogInfo($"Rolled {name} when {action}: " + string.Join(", ", items.Select(Describe)) + ".");
            }
            finally { TreasureProgression.Current = previous; }
        }

        private static string Describe(ItemDrop.ItemData item) =>
            item.m_dropPrefab.name + (item.m_stack > 1 ? " x" + item.m_stack : "") + (item.IsMagic() ? " " + item.GetRarity() : "");
    }
}
