using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // With the bulk modifier held, Use on a sap extractor empties it and then the nearest extractors in range into the
    // inventory, one owner-confirmed extraction at a time. Ordinary Use still drops the sap at the extractor.
    [HarmonyPatch]
    internal static class SapCollection
    {
        private const string CollectRpc = "ProgressionFixCollectSap";
        private const string PickupRpc = "ProgressionFixSapPickup";
        private static readonly Action<SapCollector, long> Extract = AccessTools.MethodDelegate<Action<SapCollector, long>>(
            AccessTools.Method(typeof(SapCollector), "RPC_Extract"));
        private static readonly Func<SapCollector, int> Level = AccessTools.MethodDelegate<Func<SapCollector, int>>(
            AccessTools.Method(typeof(SapCollector), "GetLevel"));
        private static readonly ConditionalWeakTable<SapCollector, Dictionary<long, (int Request, ZDOID[] Outputs)>> Receipts =
            new ConditionalWeakTable<SapCollector, Dictionary<long, (int, ZDOID[])>>();
        private static readonly Dictionary<int, Collection> Pending = new Dictionary<int, Collection>();
        private static List<GameObject> collectionOutputs;
        private static bool running;
        private static int nextRequest;

        private sealed class Collection
        {
            internal SapCollector Extractor;
            internal long Owner;
            internal List<ZDOID> Outputs;
        }

        // The extractors in range holding sap that this player may empty, target first, then nearest.
        private static List<SapCollector> Collectable(SapCollector target, Player player, float radius)
        {
            var found = new HashSet<SapCollector> { target };
            string name = Utils.GetPrefabName(target.gameObject);
            foreach (var collider in Physics.OverlapSphere(target.transform.position, radius))
            {
                var extractor = collider.GetComponentInParent<SapCollector>();
                if (extractor && Utils.GetPrefabName(extractor.gameObject) == name &&
                    (extractor.transform.position - target.transform.position).sqrMagnitude <= radius * radius) found.Add(extractor);
            }
            return found.Where(extractor => Level(extractor) > 0 && WardAccess.Allowed(extractor, player.GetPlayerID()))
                .OrderBy(extractor => extractor == target ? -1f : (extractor.transform.position - target.transform.position).sqrMagnitude).ToList();
        }

        [HarmonyPrefix, HarmonyPatch(typeof(SapCollector), nameof(SapCollector.Interact))]
        private static bool CollectNearby(SapCollector __instance, Humanoid character, bool repeat, ref bool __result)
        {
            if (repeat || character != Player.m_localPlayer || !BulkPickup.TryGetHarvestRadius(out float radius)) return true;
            var player = (Player)character;
            __result = false;
            if (running) return false;
            var extractors = Collectable(__instance, player, radius);
            if (extractors.Count == 0) return true;
            __result = true;
            player.StartCoroutine(Collect(extractors, player));
            return false;
        }

        private static IEnumerator Collect(List<SapCollector> extractors, Player player)
        {
            running = true;
            try
            {
                foreach (var extractor in extractors)
                {
                    if (!player || player != Player.m_localPlayer || player.IsDead()) yield break;
                    var view = extractor ? extractor.GetComponent<ZNetView>() : null;
                    if (!view || !view.IsValid() || Level(extractor) == 0 || !WardAccess.Allowed(extractor, player.GetPlayerID())) continue;
                    if (view.GetZDO().GetOwner() == 0L) view.ClaimOwnership();
                    int request = ++nextRequest;
                    var collection = new Collection { Extractor = extractor, Owner = view.GetZDO().GetOwner() };
                    Pending.Add(request, collection);
                    view.InvokeRPC(collection.Owner, CollectRpc, request);
                    float deadline = Time.time + 5f;
                    while (collection.Outputs == null && Time.time < deadline) yield return null;
                    Pending.Remove(request);
                    if (collection.Outputs == null)
                    {
                        if (player) player.Message(MessageHud.MessageType.Center, "The sap extractor did not respond.");
                        yield break;
                    }
                    // An empty reply means the extractor's ownership moved, or someone emptied it first.
                    if (collection.Outputs.Count == 0) yield break;
                    Game.instance.IncrementPlayerStat(PlayerStatType.SapHarvested);
                    yield return HarvestOutputPickup.Pickup(player, collection.Outputs);
                }
            }
            finally { running = false; }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(SapCollector), "Awake")]
        private static void Register(SapCollector __instance, ZNetView ___m_nview)
        {
            if (!___m_nview || !___m_nview.IsValid()) return;
            ___m_nview.Register<int>(CollectRpc, (sender, request) => HarvestOutputPickup.ReceiveCollection(___m_nview,
                Receipts.GetOrCreateValue(__instance), sender, request, PickupRpc, outputs =>
                {
                    collectionOutputs = outputs;
                    // Keep the native extraction, world yield scaling, effects and reset.
                    try { Extract(__instance, sender); }
                    finally { collectionOutputs = null; }
                }));
            ___m_nview.Register<int, ZPackage>(PickupRpc, (sender, request, package) =>
            {
                if (Pending.TryGetValue(request, out var collection) && collection.Extractor == __instance && collection.Owner == sender)
                    collection.Outputs = HarvestOutputPickup.ReadIds(package);
            });
        }

        [HarmonyTranspiler, HarmonyPatch(typeof(SapCollector), "RPC_Extract")]
        private static IEnumerable<CodeInstruction> CaptureSap(IEnumerable<CodeInstruction> instructions) =>
            HarvestOutputPickup.CaptureInstantiate<ItemDrop>(instructions, AccessTools.Method(typeof(SapCollection), nameof(RememberSap)),
                "The sap extractor's sap creation changed.");

        private static void RememberSap(ItemDrop output) => collectionOutputs?.Add(output.gameObject);

        [HarmonyPostfix, HarmonyPatch(typeof(SapCollector), nameof(SapCollector.GetHoverText))]
        private static void Hint(SapCollector __instance, ref string __result)
        {
            var player = Player.m_localPlayer;
            if (!player || !BulkPickup.TryGetHarvestRadius(out float radius, true)) return;
            if (running) { __result += BulkPickup.Hint("Collecting sap"); return; }
            int stored = Collectable(__instance, player, radius).Sum(Level);
            if (stored > 0) __result += BulkPickup.Hint($"Collect sap nearby ({stored})");
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.SetLocalPlayer))]
        private static void ResetForNewPlayer(Player __instance)
        {
            if (Player.m_localPlayer == __instance) return;
            running = false;
            Pending.Clear();
        }
    }
}
