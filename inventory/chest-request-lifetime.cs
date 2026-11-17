using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A live unanswered transfer still needs its original request if a late reply arrives. After native network
    // shutdown no reply can arrive, but MultiUserChest otherwise owns those inventories across every later world.
    // Drop only the departed session's request references; never expire, replay or refund a live transfer.
    internal static class ChestRequestLifetime
    {
        private static IDictionary pending;
        private static PropertyInfo success, ownerView;
        private static MethodInfo ownerOf;
        private static Func<Inventory, IEnumerator> removePreview;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.MultiUserChest, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var handler = assembly.GetType("MultiUserChest.PackageHandler", true);
            pending = AccessTools.Field(handler, "Packages")?.GetValue(null) as IDictionary;
            if (pending == null) throw new InvalidOperationException("MultiUserChest's pending requests changed.");
            var response = assembly.GetType("MultiUserChest.IResponse", true);
            var owner = assembly.GetType("MultiUserChest.InventoryOwner", true);
            var preview = assembly.GetType("MultiUserChest.InventoryPreview", true);
            success = AccessTools.Property(response, "Success");
            ownerView = AccessTools.Property(owner, "ZNetView");
            ownerOf = AccessTools.Method(owner, "GetOwner", new[] { typeof(Inventory) });
            if (success == null || ownerView == null || ownerOf == null) throw new InvalidOperationException("MultiUserChest's finished previews changed.");
            removePreview = AccessTools.MethodDelegate<Func<Inventory, IEnumerator>>(AccessTools.Method(preview, "RemoveQueuedPackagesDelayed"));
            harmony.Patch(AccessTools.Method(preview, "RemovePackage", new[] { typeof(Inventory), response }),
                postfix: new HarmonyMethod(typeof(ChestRequestLifetime), nameof(FinishPreview)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "Shutdown"), postfix: new HarmonyMethod(typeof(ChestRequestLifetime), nameof(Leave)));
        }

        // Replication can arrive before its successful reply. Waiting only for the next inventory change then leaves
        // the completed preview behind, sometimes displaying a doubled stack. Use the mod's existing short removal
        // delay for successful remote replies as well as refusals; it removes only acknowledged preview records.
        private static void FinishPreview(Inventory __0, object __1)
        {
            if (__0 == null || !(bool)success.GetValue(__1)) return;
            var owner = ownerOf.Invoke(null, new object[] { __0 });
            var view = owner == null ? null : ownerView.GetValue(owner) as ZNetView;
            if (!view || !view.IsOwner()) ThreadingHelper.Instance.StartCoroutine(removePreview(__0));
        }

        private static void Leave() => pending.Clear();
    }
}
