using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The table's Upgrade button pays first, then asks the table's owner, and refunds on a refusal or after 20 seconds without a reply.
    // EpicLoot forgets a request whose Player object is gone and refunds into a dead character, and a request that arrives after
    // the refund gives a free level. Here a request waits for its living character, and a success after the refund takes the cost again.
    internal static class TableUpgradeSettlement
    {
        private const float ReplyTimeout = 20f;
        private static readonly Type Pending = AccessTools.Inner(typeof(EnchantingTable), "PendingUpgrade");
        private static readonly FieldInfo Request = AccessTools.Field(Pending, "Request"), Requester = AccessTools.Field(Pending, "Requester"), SentTime = AccessTools.Field(Pending, "SentTime");
        private static readonly IList Requests = (IList)AccessTools.Field(typeof(EnchantingTable), "_upgradeRequests").GetValue(null);
        private static readonly Func<ZDOID, EnchantingFeature, int> ReadLevel =
            AccessTools.MethodDelegate<Func<ZDOID, EnchantingFeature, int>>(AccessTools.Method(typeof(EnchantingTable), "ReadFeatureLevel"));
        private static readonly Action<EnchantingFeatureUpgradeRequest, bool> Settle =
            AccessTools.MethodDelegate<Action<EnchantingFeatureUpgradeRequest, bool>>(AccessTools.Method(typeof(EnchantingTable), "SettleUpgradeRequest"));
        private static readonly ConditionalWeakTable<object, object> PlayerIds = new ConditionalWeakTable<object, object>();
        private static readonly HashSet<(ZDOID Table, EnchantingFeature Feature, int Level)> Refunded = new HashSet<(ZDOID, EnchantingFeature, int)>();

        internal static void Initialize(IntegrationPatches harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(EnchantingTable), nameof(EnchantingTable.RequestTableUpgrade)), postfix: new HarmonyMethod(typeof(TableUpgradeSettlement), nameof(NoteRequester)));
            harmony.Patch(AccessTools.Method(typeof(EnchantingTable), "ExpireUpgradeRequests"), prefix: new HarmonyMethod(typeof(TableUpgradeSettlement), nameof(BeforeExpiry)));
            harmony.Patch(AccessTools.Method(typeof(EnchantingTable), "RPC_TableUpgradeResponse"), prefix: new HarmonyMethod(typeof(TableUpgradeSettlement), nameof(BeforeReply)));
        }

        private static void NoteRequester() => PlayerIds.Add(Requests[Requests.Count - 1], Player.m_localPlayer.GetPlayerID());

        private static bool Present(object pending)
        {
            var player = Player.m_localPlayer;
            return player && !player.IsDead() && PlayerIds.TryGetValue(pending, out var id) && (long)id == player.GetPlayerID();
        }

        private static void BeforeExpiry()
        {
            if (Requests.Count == 0) return;
            float now = Time.time;
            foreach (var pending in Requests)
            {
                if (now - (float)SentTime.GetValue(pending) < ReplyTimeout) continue;
                if (!Present(pending)) { SentTime.SetValue(pending, now); continue; }
                Requester.SetValue(pending, Player.m_localPlayer);
                var request = (EnchantingFeatureUpgradeRequest)Request.GetValue(pending);
                if (ReadLevel(request.TableZDO, request.Feature) < request.ToLevel) Refunded.Add((request.TableZDO, request.Feature, request.ToLevel));
            }
        }

        // A reply for a character that can't be paid leaves its request waiting, and the expiry reads the table's level instead.
        private static bool BeforeReply(ZDOID tableZDO, int featureI, int toLevel, bool success)
        {
            var feature = (EnchantingFeature)featureI;
            bool matched = false;
            foreach (var pending in Requests)
            {
                var request = (EnchantingFeatureUpgradeRequest)Request.GetValue(pending);
                if (request.TableZDO != tableZDO || request.Feature != feature || request.ToLevel != toLevel) continue;
                if (!Present(pending)) return false;
                matched = true;
            }
            if (!matched && Refunded.Remove((tableZDO, feature, toLevel)) && success) TakeCostAgain(tableZDO, feature, toLevel);
            return true;
        }

        private static void TakeCostAgain(ZDOID table, EnchantingFeature feature, int level)
        {
            if (Player.m_localPlayer == null || Player.m_localPlayer.IsDead()) return;
            if (!Player.m_localPlayer.NoCostCheat())
                foreach (var cost in EnchantingTableUpgrades.GetUpgradeCost(feature, level))
                    InventoryManagement.Instance.RemoveItem(cost.GetItem());
            Settle(new EnchantingFeatureUpgradeRequest { TableZDO = table, Feature = feature, ToLevel = level }, true);
        }
    }
}
