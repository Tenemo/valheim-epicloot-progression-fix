using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Pickable waits 1–5 seconds after loading and then checks regrowth once a minute. PlantEverything's elapsed timer
    // can already say Ready while the fruit is still absent. Run the native check on load and when an overdue bush is
    // hovered or harvested, including PlantEasily's bulk harvest. The owner still decides whether it may regrow and
    // broadcasts the normal picked state.
    [HarmonyPatch]
    internal static class PickableRegrowth
    {
        private const string CheckRpc = "ProgressionFixCheckRegrowth";
        private static readonly Action<Pickable> UpdateRespawn = AccessTools.MethodDelegate<Action<Pickable>>(
            AccessTools.Method(typeof(Pickable), "UpdateRespawn"));
        private static readonly List<ZDO> Acquired = new List<ZDO>();
        private sealed class RegrowthRequest
        {
            internal float NextRequest;
        }

        private static readonly ConditionalWeakTable<Pickable, RegrowthRequest> Requests =
            new ConditionalWeakTable<Pickable, RegrowthRequest>();

        [HarmonyPostfix, HarmonyPatch(typeof(Pickable), "Awake")]
        private static void OnLoad(Pickable __instance, ZNetView ___m_nview)
        {
            if (__instance.m_respawnTimeMinutes <= 0f || !___m_nview.IsValid()) return;
            ___m_nview.Register(CheckRpc, (long sender) => UpdateRespawn(__instance));
            UpdateRespawn(__instance);
        }

        // A bush usually loads before a player comes close enough to own it, and its load check returns while nobody
        // owns it, so an overdue bush waited up to a minute for its timer, or until hovered. Remember each record whose
        // ownership passes to this game, and check those already loaded once the incoming data has been applied.
        [HarmonyPrefix, HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwnerInternal))]
        private static void OnOwnerChange(ZDO __instance, long uid)
        {
            if (uid != 0L && uid == ZDOMan.GetSessionID() && !__instance.IsOwner()) Acquired.Add(__instance);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
        private static void OnDataApplied()
        {
            if (Acquired.Count == 0) return;
            foreach (var data in Acquired)
            {
                // Unity's destroyed wrapper still passes a CLR type test while scene-map cleanup is pending.
                if (!data.IsOwner() || !(ZNetScene.instance?.FindInstance(data) is ZNetView instance) || instance == null) continue;
                var pickable = instance.GetComponent<Pickable>();
                if (pickable != null && pickable.m_respawnTimeMinutes > 0f && pickable.GetPicked()) UpdateRespawn(pickable);
            }
            Acquired.Clear();
        }

        // The native check keeps its own copy of the harvest time, read on load. A bush loaded before anyone picked it
        // has none, so its first check as owner replaced the saved harvest time and restarted the whole regrowth timer.
        // Start from the saved time instead; a record without one still gets the game's own initial time.
        [HarmonyPrefix, HarmonyPatch(typeof(Pickable), "UpdateRespawn")]
        private static void KeepHarvestTime(ZNetView ___m_nview, ref long ___m_pickedTime)
        {
            if (___m_pickedTime == 0L && ___m_nview.IsValid() && ___m_nview.IsOwner())
                ___m_pickedTime = ___m_nview.GetZDO().GetLong(ZDOVars.s_pickedTime, 0L);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Pickable), nameof(Pickable.GetHoverText))]
        private static void OnHover(Pickable __instance, ZNetView ___m_nview, bool ___m_picked) =>
            CheckOnUse(__instance, ___m_nview, ___m_picked);

        // PlantEasily's bulk harvest uses every segment of a vine in reach, the bare ones too. Each one it picked would let the
        // next ripen for the same press, so on a vine only the segment aimed at is checked.
        [HarmonyPrefix, HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact))]
        private static void OnInteract(Pickable __instance, ZNetView ___m_nview, bool ___m_picked, Humanoid character)
        {
            if (!__instance.TryGetComponent<Vine>(out _) || AimedAt(__instance, character)) CheckOnUse(__instance, ___m_nview, ___m_picked);
        }

        private static bool AimedAt(Pickable pickable, Humanoid character)
        {
            var hovered = (character as Player)?.GetHoverObject();
            return hovered != null && hovered.GetComponentInParent<Pickable>() == pickable;
        }

        private static void CheckOnUse(Pickable __instance, ZNetView ___m_nview, bool ___m_picked)
        {
            if (!___m_picked || __instance.m_respawnTimeMinutes <= 0f || __instance.GetEnabled == 0 ||
                !___m_nview.IsValid() || !Overdue(__instance, ___m_nview)) return;

            if (___m_nview.IsOwner())
            {
                UpdateRespawn(__instance);
                return;
            }
            // Limit each target while awaiting its owner's reply, including repeated bulk harvests of several bushes.
            var request = Requests.GetValue(__instance, pickable => new RegrowthRequest());
            if (Time.time < request.NextRequest) return;
            request.NextRequest = Time.time + 1f;
            ___m_nview.InvokeRPC(CheckRpc);
        }

        internal static bool Overdue(Pickable pickable, ZNetView view)
        {
            long pickedTime = view.GetZDO().GetLong(ZDOVars.s_pickedTime, 0L);
            return pickedTime <= 1 || (ZNet.instance.GetTime() - new DateTime(pickedTime)).TotalMinutes > pickable.m_respawnTimeMinutes;
        }
    }
}
