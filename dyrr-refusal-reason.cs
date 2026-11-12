using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    // Dyrr refuses a client that runs plugins the pack does not ship, sending its reason just before the kick. Without
    // Dyrr's Core companion the player saw only Valheim's generic kicked message, so add the reason to that screen.
    internal static class DyrrRefusalReason
    {
        private static string reason;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.Dyrr, out var plugin)) return;
            var refused = AccessTools.Method(plugin.Instance.GetType().Assembly.GetType("Dyrr.Doorman", true), "OnRefused",
                new[] { typeof(ZRpc), typeof(string) });
            if (refused == null) throw new MissingMemberException("Dyrr's refusal message changed.");
            harmony.Patch(refused, postfix: new HarmonyMethod(typeof(DyrrRefusalReason), nameof(Record)));
            harmony.Patch(AccessTools.Method(typeof(FejdStartup), "ShowConnectError"),
                postfix: new HarmonyMethod(typeof(DyrrRefusalReason), nameof(Show)) { priority = Priority.Last });
        }

        private static void Record(string why) => reason = why;

        private static void Show(ZNet.ConnectionStatus statusOverride, TMP_Text ___m_connectionFailedError)
        {
            var status = statusOverride == ZNet.ConnectionStatus.None ? ZNet.GetConnectionStatus() : statusOverride;
            if (status != ZNet.ConnectionStatus.ErrorKicked || string.IsNullOrEmpty(reason)) return;
            // The pack's ServerSync copies add empty lines for their own version checks.
            ConnectionFailureDetails.Append(___m_connectionFailedError, reason);
            reason = null;
        }
    }
}
