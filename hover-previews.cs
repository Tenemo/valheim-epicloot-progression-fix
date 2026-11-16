using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The game rebuilds the hover text every frame while the crosshair rests on an object. Station previews that read
    // chests, physics, cover or wards compute their lines here instead: at most four times a second, and at once when the
    // hovered object, its saved data, any loaded inventory or the preview's own state changes. The game's own lines stay
    // live; callers merge the cached lines into them each frame.
    [HarmonyPatch]
    internal static class HoverPreviews
    {
        internal const float RefreshSeconds = 0.25f;
        private sealed class Entry
        {
            internal object Value;
            internal float Made;
            internal int Inventories, State;
            internal uint Data;
        }
        private static readonly Dictionary<(int Target, int Kind), Entry> Entries = new Dictionary<(int, int), Entry>();
        private static readonly List<(int, int)> Stale = new List<(int, int)>();
        private static int inventories;

        // True while a preview computes, so the chest readers it calls may use this frame's chest reads.
        internal static bool Computing { get; private set; }
        // Counts every change of a loaded inventory; equal values mean nothing anyone carries or stores has changed.
        internal static int Inventories => inventories;

        // The player's and every loaded container's inventory report their changes here, a chest's too when another player's
        // change arrives. Temporary inventories, which nothing listens to, are left out.
        [HarmonyPostfix, HarmonyPatch(typeof(Inventory), "Changed")]
        private static void InventoryChanged(Inventory __instance) { if (__instance.m_onChanged != null) inventories++; }

        internal static T Get<T>(Component target, int kind, int state, Func<T> compute) where T : class
        {
            var view = target.GetComponentInParent<ZNetView>();
            uint data = view && view.IsValid() ? view.GetZDO().DataRevision : 0u;
            var key = (target.GetInstanceID(), kind);
            float now = Time.realtimeSinceStartup;
            if (Entries.TryGetValue(key, out var entry) && entry.Inventories == inventories && entry.Data == data && entry.State == state &&
                now - entry.Made < RefreshSeconds && now >= entry.Made)
                return (T)entry.Value;
            if (Entries.Count >= 32) Prune(now);
            bool outer = Computing;
            Computing = true;
            T value;
            try { value = compute(); }
            finally { Computing = outer; }
            Entries[key] = new Entry { Value = value, Made = now, Inventories = inventories, Data = data, State = state };
            return value;
        }

        private static void Prune(float now)
        {
            Stale.Clear();
            foreach (var entry in Entries)
                if (now - entry.Value.Made >= RefreshSeconds || now < entry.Value.Made) Stale.Add(entry.Key);
            foreach (var key in Stale) Entries.Remove(key);
        }

        // Hover-only state flags into one key: each true flag sets its own bit.
        internal static int Flags(bool first, bool second = false, bool third = false, bool fourth = false) =>
            (first ? 1 : 0) | (second ? 2 : 0) | (third ? 4 : 0) | (fourth ? 8 : 0);
        internal static int With(int flags, float radius) => flags | Mathf.RoundToInt(radius * 100f) << 4;

        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.SetLocalPlayer))]
        private static void ResetForNewPlayer(Player __instance) { if (Player.m_localPlayer != __instance) Entries.Clear(); }
    }

    // A preview that asks about many stations and recipes reads the same chests over and over. While a preview computes,
    // each origin's chest list, as AzuCraftyBoxes builds it for that origin and range, and each chest's name and item counts
    // are read once per frame; a chest within reach of several origins is read once. Taking items always reads the chests
    // live.
    internal static class FrameChests
    {
        internal sealed class Chest
        {
            internal object Source;
            internal Inventory Inventory;
            internal string Name;
            // Shared by every origin's entry for the same chest. Each origin keeps its own entry, as AzuCraftyBoxes gives each
            // query its own containers, so a preview's simulated spends stay per origin.
            internal Contents Read;
        }
        internal sealed class Contents
        {
            // By item prefab: the first stack and the total of every stack, and of the stacks not favorited in storage.
            internal readonly Dictionary<string, Stacks> Prefabs = new Dictionary<string, Stacks>();
            // By the item's shared name, as AzuCraftyBoxes' own count reads it.
            internal readonly Dictionary<string, int> Asked = new Dictionary<string, int>();
        }
        internal sealed class Stacks
        {
            internal ItemDrop.ItemData First, FirstUnprotected;
            internal int All, Unprotected;
        }

        private static MethodInfo query, inventory, name, count;
        private static Type ordinary;
        private static Func<bool> prevented;
        private static Func<float> range;
        private static int frame = -1, revision;
        private static readonly Dictionary<(Vector3 Origin, float Range), Chest[]> Lists = new Dictionary<(Vector3, float), Chest[]>();
        private static readonly Dictionary<object, (string Name, Contents Read)> Known = new Dictionary<object, (string, Contents)>();

        internal static void Initialize(Assembly crafty, Func<float> supplyRange)
        {
            var boxes = crafty.GetType("AzuCraftyBoxes.Util.Functions.Boxes", true);
            var container = crafty.GetType("AzuCraftyBoxes.IContainers.IContainer", true);
            query = AccessTools.Method(AccessTools.Inner(boxes, "QueryFrame"), "Get").MakeGenericMethod(typeof(Component));
            inventory = AccessTools.Method(container, "GetInventory");
            name = AccessTools.Method(container, "GetPrefabName");
            count = AccessTools.Method(container, "ItemCount");
            ordinary = crafty.GetType("AzuCraftyBoxes.IContainers.VanillaContainer", true);
            prevented = AccessTools.MethodDelegate<Func<bool>>(AccessTools.Method(crafty.GetType("AzuCraftyBoxes.Util.Functions.MiscFunctions", true), "ShouldPrevent"));
            range = supplyRange;
            if (query == null || inventory == null || name == null || count == null)
                throw new InvalidOperationException("AzuCraftyBoxes' container reads changed.");
        }

        internal static bool Ready => query != null;

        // This frame's chests around an origin in AzuCraftyBoxes' order; empty while pulling is prevented.
        internal static Chest[] Around(Component origin)
        {
            if (query == null || prevented()) return Array.Empty<Chest>();
            // An inventory change earlier in the frame makes the reads stale too.
            if (frame != Time.frameCount || revision != HoverPreviews.Inventories)
            {
                Lists.Clear(); Known.Clear();
                frame = Time.frameCount;
                revision = HoverPreviews.Inventories;
            }
            float reach = range();
            var key = (origin ? origin.transform.position : Vector3.zero, reach);
            if (Lists.TryGetValue(key, out var chests)) return chests;
            var found = new List<Chest>();
            foreach (var source in (IEnumerable)query.Invoke(null, new object[] { origin, reach }))
            {
                var held = (Inventory)inventory.Invoke(source, null);
                object identity = (object)held ?? source;
                if (!Known.TryGetValue(identity, out var known)) Known[identity] = known = ((string)name.Invoke(source, null), new Contents());
                found.Add(new Chest { Source = source, Inventory = held, Name = known.Name, Read = known.Read });
            }
            return Lists[key] = found.ToArray();
        }

        // The stacks of one item prefab in this chest, read once per chest and prefab per frame.
        internal static Stacks Of(Chest chest, string prefab)
        {
            if (chest.Inventory == null) return null;
            if (chest.Read.Prefabs.TryGetValue(prefab, out var found)) return found;
            Stacks stacks = null;
            foreach (var item in chest.Inventory.GetAllItems())
            {
                if (item?.m_shared == null || !item.m_dropPrefab || item.m_dropPrefab.name != prefab) continue;
                if (stacks == null) stacks = new Stacks();
                if (stacks.First == null) stacks.First = item;
                stacks.All += item.m_stack;
                if (FavoriteProtection.IsFavoriteInStorage(item)) continue;
                if (stacks.FirstUnprotected == null) stacks.FirstUnprotected = item;
                stacks.Unprotected += item.m_stack;
            }
            return chest.Read.Prefabs[prefab] = stacks;
        }

        // AzuCraftyBoxes' own count of an item's shared name in this chest, read once per chest and name per frame.
        internal static int Count(Chest chest, string shared)
        {
            if (chest.Read.Asked.TryGetValue(shared, out int asked)) return asked;
            if (chest.Inventory != null && chest.Source.GetType() == ordinary)
            {
                // As Inventory.CountItems, which AzuCraftyBoxes' count of an ordinary chest calls.
                foreach (var item in chest.Inventory.GetAllItems())
                    if (item?.m_shared != null && item.m_shared.m_name == shared && item.m_worldLevel >= Game.m_worldLevel) asked += item.m_stack;
            }
            else asked = (int)count.Invoke(chest.Source, new object[] { shared });
            return chest.Read.Asked[shared] = asked;
        }
    }
}
