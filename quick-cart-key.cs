using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // QuickCart 1.0.1, split out of SkilledCarryWeight, attaches the player to a nearby cart, or detaches them, on its
    // key, H by default, but ignores the key in place mode, which every build tool turns on: the hammer, the hoe, the
    // cultivator and the serving tray. Let the key work with a build tool out, detach the current cart first, and attach
    // by reticle.
    internal static class QuickCartKey
    {
        private static readonly AccessTools.FieldRef<List<Vagon>> Carts =
            AccessTools.StaticFieldRefAccess<List<Vagon>>(AccessTools.Field(typeof(Vagon), "m_instances"));
        private static readonly Func<Vagon, GameObject, bool> CanAttach =
            AccessTools.MethodDelegate<Func<Vagon, GameObject, bool>>(AccessTools.Method(typeof(Vagon), "CanAttach"));

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.QuickCart, out var plugin)) return;
            var canAttach = AccessTools.Method(plugin.Instance.GetType(), "PlayerCanAttach", Type.EmptyTypes);
            var closestCart = AccessTools.Method(plugin.Instance.GetType(), "TryGetClosestVagon");
            if (canAttach == null || closestCart == null) throw new InvalidOperationException("QuickCart's checks moved.");
            harmony.Patch(canAttach, transpiler: new HarmonyMethod(typeof(QuickCartKey), nameof(IgnorePlaceMode)));
            harmony.Patch(closestCart, prefix: new HarmonyMethod(typeof(QuickCartKey), nameof(SelectCart)));
            log.LogInfo("QuickCart's key detaches the current cart first and attaches by reticle, including with build tools.");
        }

        private static bool SelectCart(ref Vagon __0, ref bool __result)
        {
            var player = Player.m_localPlayer;
            foreach (var cart in Carts())
            {
                if (!cart || !cart.IsAttached(player)) continue;
                __0 = cart;
                __result = true;
                return false;
            }
            __0 = null;
            __result = false;
            if (!GameCamera.instance) return false;
            // UpdateHover clears the hover object in place mode. Use the same camera ray and interaction layers
            // directly so the hammer, hoe, cultivator and serving tray can still target a cart.
            var camera = GameCamera.instance.transform;
            var hits = Physics.RaycastAll(camera.position, camera.forward, 50f, GameFields.InteractMask(player));
            Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
            foreach (var hit in hits)
            {
                if (hit.collider.attachedRigidbody && hit.collider.attachedRigidbody.gameObject == player.gameObject) continue;
                var cart = hit.collider.GetComponentInParent<Vagon>();
                if (cart && !cart.InUse() && CanAttach(cart, player.gameObject))
                {
                    __0 = cart;
                    __result = true;
                }
                break;
            }
            return false;
        }

        private static IEnumerable<CodeInstruction> IgnorePlaceMode(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(AccessTools.Method(typeof(Character), nameof(Character.InPlaceMode))),
                AccessTools.Method(typeof(QuickCartKey), nameof(NeverInPlaceMode)), 1, "QuickCart's check changed.");

        private static bool NeverInPlaceMode(Character character) => false;
    }
}
