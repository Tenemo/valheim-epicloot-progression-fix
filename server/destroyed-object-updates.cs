using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The server refuses updates for an object it has seen destroyed, but a player's game recreates any object it doesn't
    // have. When the server's update for a creature crosses this game's kill of it on the network, the creature comes back
    // here and nowhere else: nothing runs it, so it hangs in the air, ignores hits and stays until the player relogs. Ending
    // such a copy before the game builds anything from it applies the server's rule here for as long as an update can
    // still be on its way. Object IDs are never reused, so nothing else is refused.
    [HarmonyPatch]
    internal static class DestroyedObjectUpdates
    {
        private const float Window = 60f;
        private static readonly Action<ZDOMan, ZDOID> Remove =
            AccessTools.MethodDelegate<Action<ZDOMan, ZDOID>>(AccessTools.Method(typeof(ZDOMan), "HandleDestroyedZDO"));
        private static readonly Dictionary<ZDOID, float> destroyed = new Dictionary<ZDOID, float>();
        private static readonly Queue<ZDOID> order = new Queue<ZDOID>();

        [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO"), HarmonyPrefix]
        private static void Remember(ZDOID uid)
        {
            if (ZNet.instance.IsServer() || destroyed.ContainsKey(uid)) return;
            destroyed[uid] = Time.time + Window;
            order.Enqueue(uid);
        }

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData"), HarmonyPostfix]
        private static void EndCopies(ZDOMan __instance)
        {
            float now = Time.time;
            while (order.Count > 0 && destroyed[order.Peek()] < now) destroyed.Remove(order.Dequeue());
            foreach (var id in order)
            {
                var copy = __instance.GetZDO(id);
                if (copy == null) continue;
                string prefab = ZNetScene.instance.GetPrefab(copy.GetPrefab())?.name ?? copy.GetPrefab().ToString();
                Debug.Log($"[EpicLoot Progression Fix] Ended a late copy of destroyed {prefab}: health {copy.GetFloat(ZDOVars.s_health, -1f)}, position {copy.GetPosition()}.");
                Remove(__instance, id);
            }
        }

        // An ID hashes through a user table that every new ZDOMan resets, so IDs from an earlier connection find nothing.
        [HarmonyPatch(typeof(ZDOMan), MethodType.Constructor, typeof(int)), HarmonyPostfix]
        private static void Forget() { destroyed.Clear(); order.Clear(); }
    }
}
