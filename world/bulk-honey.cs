using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class BulkHoney
    {
        private const string CollectRpc = "ProgressionFixCollectHoney";
        private const string PickupRpc = "ProgressionFixHoneyPickup";
        private static readonly Action<Beehive, long> Extract = AccessTools.MethodDelegate<Action<Beehive, long>>(
            AccessTools.Method(typeof(Beehive), "RPC_Extract"));
        private static readonly MethodInfo InvokeRpc = AccessTools.Method(typeof(ZNetView), nameof(ZNetView.InvokeRPC),
            new[] { typeof(string), typeof(object[]) });
        private static readonly Dictionary<int, Collection> Pending = new Dictionary<int, Collection>();
        private static readonly ConditionalWeakTable<Beehive, Dictionary<long, (int Request, ZDOID[] Outputs)>> Receipts =
            new ConditionalWeakTable<Beehive, Dictionary<long, (int, ZDOID[])>>();
        private static int nextRequest;
        private static Collection currentCollection;
        private static List<GameObject> collectionOutputs;

        private sealed class Collection
        {
            internal Beehive Hive;
            internal Player Player;
            internal long Owner;
            internal int Request;
        }

        private static bool Supported(Beehive hive) => hive != null &&
            (Utils.GetPrefabName(hive.gameObject) == "piece_beehive" || Utils.GetPrefabName(hive.gameObject) == "piece_birdnest");

        [HarmonyPrefix, HarmonyPatch(typeof(Beehive), nameof(Beehive.Interact))]
        private static void RememberCollector(Beehive __instance, Humanoid character, bool repeat, bool alt, out Collection __state)
        {
            __state = null;
            if (repeat || character != Player.m_localPlayer || !Supported(__instance) ||
                !BulkPickup.TryGetHarvestRadius(out _, alt)) return;
            currentCollection = __state = new Collection { Hive = __instance, Player = (Player)character };
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(Beehive), nameof(Beehive.Interact))]
        private static void ForgetCollector(Collection __state)
        {
            if (__state != null && currentCollection == __state) currentCollection = null;
        }

        [HarmonyTranspiler, HarmonyPatch(typeof(Beehive), "Extract")]
        private static IEnumerable<CodeInstruction> CollectIntoInventory(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(InvokeRpc),
                AccessTools.Method(typeof(BulkHoney), nameof(SendCollection)), 1, "Beehive's harvest RPC changed.");

        private static void SendCollection(ZNetView view, string method, object[] arguments)
        {
            if (currentCollection == null) { view.InvokeRPC(method, arguments); return; }
            var collection = currentCollection;
            if (view.GetZDO().GetOwner() == 0L) view.ClaimOwnership();
            collection.Owner = view.GetZDO().GetOwner();
            collection.Request = ++nextRequest;
            Pending.Add(collection.Request, collection);
            collection.Player.StartCoroutine(Expire(collection));
            view.InvokeRPC(collection.Owner, CollectRpc, collection.Request);
        }

        private static IEnumerator Expire(Collection collection)
        {
            float deadline = Time.time + 5f;
            while (Pending.ContainsKey(collection.Request) && collection.Player != null &&
                !collection.Player.IsDead() && Time.time < deadline) yield return null;
            Pending.Remove(collection.Request);
        }

        [HarmonyTranspiler, HarmonyPatch(typeof(Beehive), "RPC_Extract")]
        private static IEnumerable<CodeInstruction> CaptureHoney(IEnumerable<CodeInstruction> instructions) =>
            HarvestOutputPickup.CaptureInstantiate<ItemDrop>(instructions, AccessTools.Method(typeof(BulkHoney), nameof(RememberHoney)),
                "Beehive's honey creation changed.");

        // Better Beehives creates bonus queens in its own method; only native honey or feathers are collected here.
        private static void RememberHoney(ItemDrop output) => collectionOutputs?.Add(output.gameObject);

        [HarmonyPostfix, HarmonyPatch(typeof(Beehive), "Awake")]
        private static void RegisterReplies(Beehive __instance, ZNetView ___m_nview)
        {
            if (!Supported(__instance) || !___m_nview.IsValid()) return;
            ___m_nview.Register<int>(CollectRpc, (sender, request) => ReceiveCollection(__instance, ___m_nview, sender, request));
            ___m_nview.Register<int, ZPackage>(PickupRpc, (sender, request, package) => ReceivePickup(__instance, sender, request, package));
        }

        private static void ReceiveCollection(Beehive hive, ZNetView view, long sender, int request) =>
            HarvestOutputPickup.ReceiveCollection(view, Receipts.GetOrCreateValue(hive), sender, request, PickupRpc, outputs =>
            {
                collectionOutputs = outputs;
                // Keep the native harvest, world yield scaling, effects, hive reset and queen-bee roll.
                try { Extract(hive, sender); }
                finally { collectionOutputs = null; }
            });

        private static void ReceivePickup(Beehive hive, long sender, int request, ZPackage package)
        {
            if (!Pending.TryGetValue(request, out var collection) || collection.Hive != hive || collection.Owner != sender) return;
            var ids = HarvestOutputPickup.ReadIds(package);
            Pending.Remove(request);
            if (collection.Player == null || collection.Player != Player.m_localPlayer) return;
            collection.Player.StartCoroutine(HarvestOutputPickup.Pickup(collection.Player, ids));
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.SetLocalPlayer))]
        private static void ResetForNewPlayer(Player __instance)
        {
            if (Player.m_localPlayer == __instance) return;
            Pending.Clear();
            currentCollection = null;
        }
    }
}
