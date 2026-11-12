using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class ChestSupplyEligibility
    {
        private static readonly int Excluded = "AzuCraftyBoxes_NoCrafty".GetStableHashCode();
        private static readonly Dictionary<Container, (ZDO Data, bool Excluded, long Creator, long Owner, int InUse)> States =
            new Dictionary<Container, (ZDO, bool, long, long, int)>();
        // Each chest's record, its id and revisions when its values were last read. Every write to a record raises its data
        // revision and every change of owner its owner revision, so a chest whose record still matches has the same values.
        private static readonly Dictionary<Container, (ZDO Data, ZDOID Id, uint DataRevision, ushort OwnerRevision)> Read =
            new Dictionary<Container, (ZDO, ZDOID, uint, ushort)>();
        // The isolated test client sets false to compare reading every chest's values on each query.
        internal static bool SkipUnchanged = true;
        private static HashSet<Container> containers;
        private static Action flushRegistry, invalidateCounts;
        private static FieldInfo queryFrame, queryTime, bankFrame;
        private static FieldInfo cookingItem, cookingSource;
        private static MethodInfo cookingQuery, inventory;
        private static BepInEx.Configuration.ConfigEntry<float> supplyRange;
        private static Func<bool> prevented;
        private static string mode;
        private static Player player;
        private static Vector3 origin;
        private static float range;
        private static bool hasOrigin, forgotten;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var plugin)) return;
            harmony.Patch(AccessTools.Method(typeof(Container), "Awake"), postfix: new HarmonyMethod(typeof(ChestSupplyEligibility), nameof(Watch)));
            var assembly = plugin.Instance.GetType().Assembly;
            var boxes = assembly.GetType("AzuCraftyBoxes.Util.Functions.Boxes", true);
            var frame = AccessTools.Inner(boxes, "QueryFrame");
            containers = (HashSet<Container>)AccessTools.Field(boxes, "Containers").GetValue(null);
            flushRegistry = AccessTools.MethodDelegate<Action>(AccessTools.Method(boxes, "UpdateContainers"));
            invalidateCounts = AccessTools.MethodDelegate<Action>(AccessTools.Method(boxes, "InvalidateCounts"));
            queryFrame = AccessTools.Field(frame, "FrameId");
            queryTime = AccessTools.Field(boxes, "_lastQueryTime");
            bankFrame = AccessTools.Field(assembly.GetType("AzuCraftyBoxes.Util.Functions.UiItemBank", true), "_frameId");
            if (queryFrame == null || queryTime == null || bankFrame == null) throw new InvalidOperationException("Chest supply caches changed.");
            var prefix = new HarmonyMethod(typeof(ChestSupplyEligibility), nameof(BeforeQuery));
            harmony.Patch(AccessTools.Method(frame, "Get").MakeGenericMethod(typeof(Component)), prefix: prefix);
            harmony.Patch(AccessTools.Method(boxes, "GetNearbyContainers").MakeGenericMethod(typeof(Component)), prefix: prefix);
            harmony.Patch(AccessTools.Method(boxes, "IsInList"), prefix: new HarmonyMethod(typeof(ChestSupplyEligibility), nameof(FlushRegistry)));
            var selection = assembly.GetType("AzuCraftyBoxes.Patches.CookingStationFindCookableItemPatch", true);
            cookingItem = AccessTools.Field(selection, "Item"); cookingSource = AccessTools.Field(selection, "Source");
            cookingQuery = AccessTools.Method(frame, "Get").MakeGenericMethod(typeof(CookingStation));
            inventory = AccessTools.Method(assembly.GetType("AzuCraftyBoxes.IContainers.IContainer", true), "GetInventory");
            prevented = AccessTools.MethodDelegate<Func<bool>>(AccessTools.Method(assembly.GetType("AzuCraftyBoxes.Util.Functions.MiscFunctions", true), "ShouldPrevent"));
            if (!plugin.Instance.Config.TryGetEntry("2 - CraftyBoxes", "Container Range", out supplyRange))
                throw new InvalidOperationException("Chest supply range changed.");
            harmony.Patch(AccessTools.Method(typeof(CookingStation), "CookItem"),
                prefix: new HarmonyMethod(typeof(ChestSupplyEligibility), nameof(CheckCookingSource)) { priority = Priority.First });
        }

        private static void Watch(Container __instance)
        {
            var lifetime = __instance.gameObject.AddComponent<SupplyLifetime>();
            lifetime.Container = __instance;
        }

        private sealed class SupplyLifetime : MonoBehaviour
        {
            internal Container Container;
            private void OnDestroy() => Forget(Container);
        }

        private static void FlushRegistry() => flushRegistry();

        // A destroyed chest's state goes with it; the next query rebuilds the rest and refreshes the supply caches.
        internal static void Forget(Container chest) { forgotten |= States.Remove(chest); Read.Remove(chest); }

        private static bool CheckCookingSource(CookingStation __instance, ItemDrop.ItemData item, ref bool __result)
        {
            if (item != cookingItem.GetValue(null) || cookingSource.GetValue(null) == null) return true;
            var source = inventory.Invoke(cookingSource.GetValue(null), null);
            if (!prevented())
                foreach (var candidate in (IEnumerable)cookingQuery.Invoke(null, new object[] { __instance, supplyRange.Value }))
                    if (ReferenceEquals(inventory.Invoke(candidate, null), source)) return true;
            cookingItem.SetValue(null, null); cookingSource.SetValue(null, null);
            __result = false;
            return false;
        }

        internal static void Refresh()
        {
            if (containers == null) return;
            flushRegistry();
            var currentPlayer = Player.m_localPlayer;
            string currentMode = currentPlayer && currentPlayer.m_customData.TryGetValue("ACB_PreventPulling", out var saved) ? saved : null;
            bool changed = forgotten || currentPlayer != player || currentMode != mode || States.Count != containers.Count;
            if (!changed && SkipUnchanged && Unchanged()) return;
            foreach (var chest in containers)
            {
                var view = chest ? GameFields.ContainerView(chest) : null;
                var data = view && view.IsValid() ? view.GetZDO() : null;
                var state = (data, data?.GetBool(Excluded) ?? false, data?.GetLong(ZDOVars.s_creator) ?? 0L,
                    data?.GetOwner() ?? 0L, data?.GetInt(ZDOVars.s_inUse) ?? 0);
                if (!States.TryGetValue(chest, out var previous) || !state.Equals(previous)) changed = true;
            }
            if (!changed) { Remember(); return; }
            States.Clear();
            foreach (var chest in containers)
            {
                var view = chest ? GameFields.ContainerView(chest) : null;
                var data = view && view.IsValid() ? view.GetZDO() : null;
                States[chest] = (data, data?.GetBool(Excluded) ?? false, data?.GetLong(ZDOVars.s_creator) ?? 0L,
                    data?.GetOwner() ?? 0L, data?.GetInt(ZDOVars.s_inUse) ?? 0);
            }
            player = currentPlayer; mode = currentMode; forgotten = false;
            Remember();
            Invalidate();
        }

        private static (ZDO, ZDOID, uint, ushort) Revisions(Container chest)
        {
            var view = chest ? GameFields.ContainerView(chest) : null;
            var data = view && view.IsValid() ? view.GetZDO() : null;
            return data == null ? (null, ZDOID.None, 0u, (ushort)0) : (data, data.m_uid, data.DataRevision, data.OwnerRevision);
        }

        private static bool Unchanged()
        {
            if (Read.Count != containers.Count) return false;
            foreach (var chest in containers)
                if (!Read.TryGetValue(chest, out var read) || !read.Equals(Revisions(chest))) return false;
            return true;
        }

        private static void Remember()
        {
            Read.Clear();
            foreach (var chest in containers) Read[chest] = Revisions(chest);
        }

        private static void Invalidate()
        {
            queryFrame.SetValue(null, -1);
            queryTime.SetValue(null, float.NegativeInfinity);
            bankFrame.SetValue(null, -1);
            invalidateCounts();
            CraftingMaxAmount.InvalidateSupply();
        }

        private static void BeforeQuery(Component __0, float __1)
        {
            Refresh();
            var position = __0 ? __0.transform.position : Vector3.zero;
            if (!hasOrigin || position != origin || __1 != range)
            {
                origin = position; range = __1; hasOrigin = true;
                Invalidate();
            }
        }
    }
}
