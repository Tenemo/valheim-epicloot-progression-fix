using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Beyond the native ownership area, food can have no owner or an owner that no longer loads it. The native
    // RemoveOne request then has nobody to answer. Let the server hand that food to the animal's current simulator;
    // the animal still finds, approaches, consumes and saves the item through the native feeding path.
    internal static class CreatureFeeding
    {
        private const string Request = "EpicLootProgressionFix_AnimalFood";
        private static MonsterAI consumer;
        private static readonly AccessTools.FieldRef<ItemDrop, float> LastRequest = AccessTools.FieldRefAccess<ItemDrop, float>("m_lastOwnerRequest");
        private static readonly AccessTools.FieldRef<ItemDrop, float> RetryDelay = AccessTools.FieldRefAccess<ItemDrop, float>("m_ownerRetryTimeout");
        private static readonly AccessTools.FieldRef<MonsterAI, ItemDrop> Food = AccessTools.FieldRefAccess<MonsterAI, ItemDrop>("m_consumeTarget");
        private static readonly AccessTools.FieldRef<BaseAI, bool> SerpentMovement = AccessTools.FieldRefAccess<BaseAI, bool>("m_serpentMovement");

        private static float SearchRange(MonsterAI animal) => Mathf.Max(30f, animal.m_consumeSearchRange);
        private static readonly Func<MonsterAI, ItemDrop.ItemData, bool> CanConsume =
            AccessTools.MethodDelegate<Func<MonsterAI, ItemDrop.ItemData, bool>>(AccessTools.Method(typeof(MonsterAI), "CanConsume"));
        private static readonly Func<BaseAI, Vector3, bool> HavePath =
            AccessTools.MethodDelegate<Func<BaseAI, Vector3, bool>>(AccessTools.Method(typeof(BaseAI), "HavePath"));

        [HarmonyPatch(typeof(MonsterAI), "FindClosestConsumableItem")]
        private static class Selection
        {
            private static bool Prefix(MonsterAI __instance, float maxRange, ref ItemDrop __result)
            {
                var origin = __instance.transform.position;
                __result = Physics.OverlapSphere(origin, maxRange, LayerMask.GetMask("item"))
                    .Select(collider => collider.attachedRigidbody ? collider.attachedRigidbody.GetComponent<ItemDrop>() : null)
                    .Where(item => item && item.GetComponent<ZNetView>().IsValid() && CanConsume(__instance, item.m_itemData))
                    .Distinct().OrderBy(item => (item.transform.position - origin).sqrMagnitude)
                    .FirstOrDefault(item => HavePath(__instance, item.transform.position));
                return false;
            }
        }

        [HarmonyPatch]
        private static class Registration
        {
            private static MethodBase TargetMethod() => AccessTools.Constructor(typeof(ZRoutedRpc), new[] { typeof(bool) });
            private static void Postfix(ZRoutedRpc __instance) => __instance.Register<ZDOID, ZDOID>(Request, Receive);
        }

        private static void Receive(long sender, ZDOID creatureId, ZDOID foodId)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZNetScene.instance == null) return;
            var creature = ZDOMan.instance.GetZDO(creatureId);
            var food = ZDOMan.instance.GetZDO(foodId);
            if (creature == null || food == null || creature.GetOwner() != sender || !(creature.GetFloat(ZDOVars.s_health, 1f) > 0f) || !CreatureOwnership.CanOwn(creature, sender)) return;
            var creaturePrefab = ZNetScene.instance.GetPrefab(creature.GetPrefab());
            var foodPrefab = ZNetScene.instance.GetPrefab(food.GetPrefab());
            var diet = creaturePrefab == null ? null : creaturePrefab.GetComponent<MonsterAI>();
            var item = foodPrefab == null ? null : foodPrefab.GetComponent<ItemDrop>();
            if (diet == null || item == null || diet.m_consumeItems == null ||
                !diet.m_consumeItems.Any(allowed => allowed != null && allowed.m_itemData.m_shared.m_name == item.m_itemData.m_shared.m_name)) return;
            // Native search includes collider bounds; allow one meter for those bounds and position replication.
            if (!(Vector3.Distance(creature.GetPosition(), food.GetPosition()) <= Mathf.Max(SearchRange(diet), diet.m_consumeRange) + 1f)) return;
            long owner = food.GetOwner();
            if (owner == sender || owner != 0 && CreatureOwnership.CanOwn(food, owner)) return;
            food.SetOwner(sender);
        }

        [HarmonyPatch(typeof(MonsterAI), "UpdateConsumeItem")]
        private static class Consumption
        {
            private static void Prefix(MonsterAI __instance, out (MonsterAI Consumer, float Range) __state)
            {
                __state = (consumer, __instance.m_consumeSearchRange);
                consumer = __instance;
                __instance.m_consumeSearchRange = SearchRange(__instance);
            }
            private static void Finalizer(MonsterAI __instance, (MonsterAI Consumer, float Range) __state)
            {
                __instance.m_consumeSearchRange = __state.Range;
                consumer = __state.Consumer;
            }
        }

        [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.RequestOwn))]
        private static class FoodRequest
        {
            private static void Prefix(ItemDrop __instance)
            {
                if (consumer == null || Time.time - LastRequest(__instance) < RetryDelay(__instance)) return;
                var animal = consumer.GetComponent<ZNetView>();
                var food = __instance.GetComponent<ZNetView>();
                if (animal == null || !animal.IsValid() || !animal.IsOwner() || food == null || !food.IsValid() || food.IsOwner()) return;
                if (ZNet.instance.IsServer()) Receive(ZDOMan.GetSessionID(), animal.GetZDO().m_uid, food.GetZDO().m_uid);
                else
                {
                    var server = ZNet.instance.GetServerPeer();
                    if (server != null) ZRoutedRpc.instance.InvokeRoutedRPC(server.m_uid, Request, animal.GetZDO().m_uid, food.GetZDO().m_uid);
                }
                // Native RequestOwn keeps its existing backoff and still asks an active owner directly.
            }
        }

        [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.RemoveOne))]
        private static class ReachedFood
        {
            private static bool Prefix(ItemDrop __instance, ref bool __result)
            {
                if (!consumer || Food(consumer) != __instance) return true;
                // Native MoveTo also returns true when a path fails. Only its actual arrival distance permits eating.
                float distance = Mathf.Max(consumer.m_consumeRange, SerpentMovement(consumer) ? 3f : .5f);
                if (Utils.DistanceXZ(consumer.transform.position, __instance.transform.position) < distance) return true;
                __result = false; return false;
            }
        }
    }
}
