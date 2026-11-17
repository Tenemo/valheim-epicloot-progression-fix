using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class CorpseLootDelivery
    {
        private static readonly MethodInfo Spawn = AccessTools.Method(typeof(Ragdoll), "SpawnLoot");

        // The instant-loot client drops in Start and removes SpawnLoot from DestroyNow. A corpse can change owners
        // after Start, so finish any still-pending native loot before destruction on either kind of owner.
        [HarmonyPatch(typeof(Ragdoll), "DestroyNow")]
        private static class FinishPending
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Ragdoll __instance, bool __runOriginal)
            {
                var view = __instance.GetComponent<ZNetView>();
                if (!__runOriginal || !view || !view.IsValid() || !view.IsOwner()) return;
                var data = view.GetZDO();
                if (data.GetInt(ZDOVars.s_drops) <= 0 && data.GetString("characterName").Length == 0) return;
                Spawn.Invoke(__instance, new object[] { __instance.m_lootSpawnJoint ? __instance.m_lootSpawnJoint.transform.position : __instance.GetAverageBodyPosition() });
            }
        }

        // Consume the native records after all resource and EpicLoot postfixes finish. Empty records survive saves,
        // reloads and owner handoffs; a second native call then has neither resources nor an EpicLoot death to roll.
        [HarmonyPatch(typeof(Ragdoll), "SpawnLoot")]
        private static class Consumed
        {
            private static void Finalizer(Ragdoll __instance, Exception __exception)
            {
                var view = __instance.GetComponent<ZNetView>();
                if (__exception != null || !view || !view.IsValid() || !view.IsOwner()) return;
                var data = view.GetZDO();
                data.Set(ZDOVars.s_drops, 0);
                data.Set("characterName", "");
                data.Set("el-lky-rolls", 0);
            }
        }
    }
}
