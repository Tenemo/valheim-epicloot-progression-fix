using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using EpicLoot;
using EpicLoot.ShardStones;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class ShardstoneMigration
    {
        private static bool queued, repairing;
        private static readonly HashSet<string> RefreshedEffects = new HashSet<string> {
            MagicEffectType.UseAdrenalineAsStamina, MagicEffectType.PerfectDodgeGivesSpeed, MagicEffectType.RestingHealthRegen,
            MagicEffectType.IncreaseMiningDrop, MagicEffectType.IncreaseTreeDrop,
            MagicEffectType.DaySailingSpeed, MagicEffectType.SailingSpeed };
        private static readonly System.Action<Container> SaveContainer = AccessTools.MethodDelegate<System.Action<Container>>(
            AccessTools.Method(typeof(Container), "Save"));
        // The container data revision each owned container was last checked at, so reads between changes cost a lookup.
        private static readonly ConditionalWeakTable<Container, StrongBox<uint>> CheckedRevisions = new ConditionalWeakTable<Container, StrongBox<uint>>();
        private static readonly System.Action<ItemDrop> SaveDrop = AccessTools.MethodDelegate<System.Action<ItemDrop>>(
            AccessTools.Method(typeof(ItemDrop), "Save", System.Type.EmptyTypes));
        private static bool Ready => Plugin.Ready && ZNetScene.instance != null && ObjectDB.instance != null &&
            MagicItemEffectDefinitions.AllDefinitions.Count > 0;

        internal static int Repair(ItemDrop.ItemData item, Inventory destination, Vector3 overflowPosition)
        {
            var magic = item?.GetMagicItem();
            if (!Ready || magic == null || magic.Sockets.Count == 0 || item.m_stack < 1) return 0;
            if (SocketConflicts.Returns(item, magic).Count == 0) return 0;
            // An open socket panel writes its live sockets over the stored copy; judge those, so a compatible panel stays open.
            if (RerollShardstones.SaveOpenSockets(item) && SocketConflicts.Returns(item, magic = item.GetMagicItem()).Count == 0) return 0;
            RerollShardstones.CloseOpenSockets(item);
            var incompatible = SocketConflicts.Returns(item, magic);
            if (incompatible.Count == 0) return 0;
            var returned = new List<ItemDrop.ItemData>();
            foreach (var socket in incompatible)
            {
                var stone = ShardSocketManager.ReconstructShardItem(socket);
                if (stone == null) return 0;
                // A legacy stack shares one saved socket list, so every unit must receive its stone back.
                for (int quantity = 0; quantity < item.m_stack; quantity++) returned.Add(stone.Clone());
            }
            if (!RerollShardstones.Deliver(item, magic, incompatible, returned, destination, overflowPosition)) return 0;
            item.SaveMagicItem(magic);
            RerollShardstones.NotifyReturns(destination);
            return returned.Count;
        }

        internal static bool RefreshValues(ItemDrop.ItemData item, bool panelSaved = false)
        {
            var magic = item?.GetMagicItem();
            if (!Ready || magic == null || magic.Sockets.Count == 0) return false;
            var affected = magic.Sockets.Select(socket => socket != null && socket.ShardType != ShardType.None &&
                RefreshedEffects.Contains(Shards.GetShardEffect(item, socket.ShardType)?.EffectType)).ToArray();
            if (!affected.Any(value => value)) return false;
            // Recompute with EpicLoot's rarity and stacking rules on copies; other saved effects and runes stay intact.
            var preview = new MagicItem { Sockets = magic.Sockets.Select(socket => socket == null ? null :
                new SocketedEffect(socket.Effect, socket.SourcePrefab, socket.SourceRarity) {
                    ShardType = socket.ShardType, StackMultiplier = socket.StackMultiplier }).ToList() };
            ShardSocketManager.RecomputeSocketValues(item, preview);
            var changed = Enumerable.Range(0, affected.Length).Where(index => affected[index] &&
                (magic.Sockets[index].Effect?.EffectType != preview.Sockets[index].Effect?.EffectType ||
                 magic.Sockets[index].Effect?.EffectValue != preview.Sockets[index].Effect?.EffectValue ||
                 magic.Sockets[index].StackMultiplier != preview.Sockets[index].StackMultiplier)).ToArray();
            if (changed.Length == 0) return false;
            // The panel's own save recomputes its live sockets with the same rules and keeps it open.
            if (!panelSaved && RerollShardstones.SaveOpenSockets(item))
            {
                RefreshValues(item, panelSaved: true);
                return true;
            }
            if (OpenSockets.Item == item)
            {
                RerollShardstones.CloseOpenSockets(item);
                return RefreshValues(item, panelSaved: true);
            }
            foreach (int index in changed)
            {
                magic.Sockets[index].Effect = preview.Sockets[index].Effect;
                magic.Sockets[index].StackMultiplier = preview.Sockets[index].StackMultiplier;
            }
            item.SaveMagicItem(magic);
            return true;
        }

        private static int RepairInventory(Inventory inventory, Vector3 position, out bool changed)
        {
            changed = false;
            if (!Ready || repairing || inventory == null) return 0;
            repairing = true;
            try
            {
                int returned = 0;
                bool refreshed = false;
                foreach (var item in inventory.GetAllItems().ToArray())
                {
                    returned += Repair(item, inventory, position);
                    refreshed |= RefreshValues(item);
                }
                if (refreshed) RerollShardstones.NotifyReturns(inventory);
                changed = returned > 0 || refreshed;
                return returned;
            }
            finally { repairing = false; }
        }

        [HarmonyPatch(typeof(Game), "SpawnPlayer"), HarmonyPostfix]
        private static void Spawned() => QueuePlayer(Player.m_localPlayer);

        [HarmonyPatch(typeof(Player), "OnInventoryChanged"), HarmonyPostfix]
        private static void InventoryChanged(Player __instance) => QueuePlayer(__instance);

        private static void QueuePlayer(Player player)
        {
            if (queued || repairing || !Ready || player == null || player != Player.m_localPlayer || Game.instance == null) return;
            queued = true;
            Game.instance.StartCoroutine(RepairPlayer(player));
        }

        private static IEnumerator RepairPlayer(Player player)
        {
            // Let character load, equipment slots and favorites finish before returning anything.
            yield return null;
            queued = false;
            if (player == null || player != Player.m_localPlayer || player.IsDead()) yield break;
            int returned = RepairInventory(player.GetInventory(), player.transform.position + Vector3.up, out _);
            if (returned > 0) player.Message(MessageHud.MessageType.Center, "Returned incompatible shardstones and runes: " + returned + ". Full inventory overflow is on the ground.");
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPrefix]
        private static void Leaving() { queued = false; }

        // Crafting and building count nearby chests through GetInventory every frame, so an owned container is repaired once
        // per change of its saved contents, including ones that arrived while another game owned it, instead of on every read.
        [HarmonyPatch(typeof(Container), "GetInventory"), HarmonyPostfix]
        private static void ContainerInventory(Container __instance) => CheckContainer(__instance);

        [HarmonyPatch(typeof(Container), "Load"), HarmonyPostfix]
        private static void ContainerLoaded(Container __instance, bool __result)
        {
            if (__result) CheckContainer(__instance);
        }

        private static void CheckContainer(Container container)
        {
            // AzuCraftyBoxes can hold a chest that has since unloaded; the destroyed object has nothing to repair.
            if (!container || !Ready || repairing) return;
            var view = GameFields.ContainerView(container);
            if (view == null || !view.IsValid() || !view.IsOwner()) return;
            var data = view.GetZDO();
            var checkedRevision = CheckedRevisions.GetValue(container, _ => new StrongBox<uint>(uint.MaxValue));
            if (checkedRevision.Value == data.DataRevision) return;
            RepairInventory(GameFields.ContainerInventory(container), container.transform.position + Vector3.up, out bool changed);
            if (changed) SaveContainer(container);
            checkedRevision.Value = data.DataRevision;
        }

        [HarmonyPatch(typeof(ItemDrop), "Start"), HarmonyPostfix]
        private static void GroundStarted(ItemDrop __instance) => GroundItem(__instance);

        [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.Load)), HarmonyPostfix]
        private static void GroundLoaded(ItemDrop __instance) => GroundItem(__instance);

        [HarmonyPatch(typeof(ItemDrop), "SlowUpdate"), HarmonyPostfix]
        private static void GroundUpdated(ItemDrop __instance) => GroundItem(__instance);

        private static void GroundItem(ItemDrop __instance)
        {
            var view = __instance.GetComponent<ZNetView>();
            if (!Ready || repairing || view == null || !view.IsValid() || !view.IsOwner()) return;
            repairing = true;
            try
            {
                int returned = Repair(__instance.m_itemData, null, __instance.transform.position + Vector3.up);
                if (RefreshValues(__instance.m_itemData) || returned > 0) SaveDrop(__instance);
            }
            finally { repairing = false; }
        }
    }
}
