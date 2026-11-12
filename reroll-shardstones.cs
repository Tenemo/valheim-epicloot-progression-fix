using System;
using System.Collections.Generic;
using System.Linq;
using EpicLoot;
using EpicLoot.Crafting;
using EpicLoot.MagicItemEffects;
using EpicLoot.ShardStones;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class RerollShardstones
    {
        private static readonly Func<ShardType, bool> AllowMatchingEffect = AccessTools.MethodDelegate<Func<ShardType, bool>>(
            AccessTools.Method(typeof(ShardSocketManager), "AllowMatchingItemEffect"));
        private static readonly Action<Inventory, bool, bool> NotifyInventory = AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(
            AccessTools.Method(typeof(Inventory), "Changed"));
        private static readonly Action SaveSockets = AccessTools.MethodDelegate<Action>(AccessTools.Method(typeof(ShardUI), "SaveSockets"));
        private static AugmentChoiceDialog blockedDialog;

        // The native button closes the dialog after its callback. A rejected choice must keep those paid rolls available.
        [HarmonyPatch(typeof(AugmentChoiceDialog), nameof(AugmentChoiceDialog.Show)), HarmonyPrefix]
        private static void Show(AugmentChoiceDialog __instance,
            ref Action<ItemDrop.ItemData, int, MagicItemEffect> onCompleteCallback)
        {
            blockedDialog = null;
            var complete = onCompleteCallback;
            var dialog = __instance;
            onCompleteCallback = (item, index, effect) =>
            {
                int previousSockets = item?.GetMagicItem()?.Sockets.Count ?? 0;
                blockedDialog = dialog;
                if (!Apply(item, index, effect, () => complete(item, index, effect)))
                {
                    return;
                }
                blockedDialog = null;
                int returned = previousSockets - item.GetMagicItem().Sockets.Count;
                if (returned > 0)
                {
                    NotifyInventory(Player.m_localPlayer.GetInventory(), false, false);
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Returned incompatible runes and shardstones: " + returned + ".");
                }
            };
        }

        [HarmonyPatch(typeof(AugmentChoiceDialog), nameof(AugmentChoiceDialog.OnClose)), HarmonyPrefix]
        private static bool Close(AugmentChoiceDialog __instance) => __instance != blockedDialog;

        internal static bool Compatible(ItemDrop.ItemData item, MagicItem magic, SocketedEffect socket)
        {
            if (socket == null || socket.ShardType == ShardType.None || socket.Effect == null) return true;
            if (!MagicItemEffectDefinitions.TryGet(socket.Effect.EffectType, out var definition)) return false;
            bool compatible = definition.Requirements.CheckRequirements(item, magic, out var failure, out var conflict,
                socket.Effect.EffectType, checklootroll: false, checkaugmentroll: false, checkruneroll: true,
                checkItemTypeGating: false);
            return compatible || failure == RequirementFailure.ConflictingEffect && conflict == socket.Effect.EffectType &&
                AllowMatchingEffect(socket.ShardType);
        }

        internal static void CloseOpenSockets(ItemDrop.ItemData item)
        {
            if (ShardUI.IsOpenFor(item)) ShardUI.Close();
        }

        // Writes an open socket panel's live sockets to its item through the panel's own save, leaving the panel open.
        internal static bool SaveOpenSockets(ItemDrop.ItemData item)
        {
            if (!ShardUI.IsOpenFor(item)) return false;
            SaveSockets();
            return true;
        }

        internal static bool ReturnIncompatible(ItemDrop.ItemData item, int index, MagicItemEffect replacement) =>
            Apply(item, index, replacement, null);

        private static bool Apply(ItemDrop.ItemData item, int index, MagicItemEffect replacement, Action complete)
        {
            var player = Player.m_localPlayer;
            var inventory = player?.GetInventory();
            var magic = item?.GetMagicItem();
            if (inventory == null || !inventory.ContainsItem(item) || magic == null || replacement == null ||
                index < 0 || index >= magic.Effects.Count)
            {
                player?.Message(MessageHud.MessageType.Center, "The item is no longer available for this reroll.");
                return false;
            }
            var original = magic.Effects[index];
            if (original.EffectType == replacement.EffectType && original.EffectValue == replacement.EffectValue)
            {
                complete?.Invoke();
                return true;
            }
            if (!AffixReplacements.AugmentEffects(item, magic, index, false, true, false, index)
                .Any(definition => definition.Type == replacement.EffectType))
            {
                player.Message(MessageHud.MessageType.Center, "This effect is no longer available for this reroll.");
                return false;
            }
            // Close a stale E-opened socket inventory before it can write the removed stones back into the item.
            CloseOpenSockets(item);
            magic = item.GetMagicItem();
            original = magic.Effects[index];

            var removed = new List<SocketedEffect>();
            var returned = new List<ItemDrop.ItemData>();
            var sockets = magic.Sockets;
            var shared = item.m_shared;
            magic.Effects[index] = replacement;
            try
            {
                var occupants = new Dictionary<SocketedEffect, ItemDrop.ItemData>();
                foreach (var socket in sockets)
                {
                    // Reconstruction normalizes legacy rune history. Do that on a copy until the return is committed.
                    var copied = socket == null ? null : new SocketedEffect(socket.Effect == null ? null :
                        RuneExtractionPower.CopyEffect(socket.Effect.EffectType, socket.Effect.EffectValue, socket.Effect),
                        socket.SourcePrefab, socket.SourceRarity) { ShardType = socket.ShardType, StackMultiplier = socket.StackMultiplier };
                    var stone = ShardSocketManager.ReconstructShardItem(copied);
                    if (stone == null)
                    {
                        player.Message(MessageHud.MessageType.Center, "This socket cannot be replaced. Choose another effect.");
                        return false;
                    }
                    SocketFavorites.RestoreReturned(item, sockets.IndexOf(socket), stone);
                    occupants.Add(socket, stone);
                }
                var retained = sockets.OrderBy(socket => socket.ShardType != ShardType.None).ToList();
                bool changed;
                do
                {
                    changed = false;
                    // Later occupants yield to earlier ones. Repeat because an ejection can remove another rune's prerequisite.
                    for (int candidate = retained.Count - 1; candidate >= 0; candidate--)
                    {
                        var socket = retained[candidate];
                        magic.Sockets = retained.Where(other => other != socket).ToList();
                        item.m_shared = shared;
                        if (!shared.m_useDurability && !magic.HasEffect(MagicEffectType.Indestructible, true) &&
                            Indestructible.OriginallyUsesDurability(item))
                        {
                            item.m_shared = RestoreEffectAfterPreview.CloneShared(shared);
                            item.m_shared.m_useDurability = true;
                        }
                        if (EnchantmentApplicability.KeepingOccupants(() => ShardSocketManager.CanCoexist(item, occupants[socket], magic.Sockets.Select(other => occupants[other]), out _))) continue;
                        retained.RemoveAt(candidate);
                        changed = true;
                    }
                } while (changed);
                foreach (var socket in sockets.Where(socket => !retained.Contains(socket)))
                {
                    removed.Add(socket);
                    returned.Add(occupants[socket]);
                }
            }
            finally
            {
                item.m_shared = shared;
                magic.Sockets = sockets;
                magic.Effects[index] = original;
            }
            if (removed.Count == 0)
            {
                complete?.Invoke();
                return true;
            }
            return DeliverAndComplete(item, magic, removed, returned, inventory,
                player.transform.position + player.transform.forward + Vector3.up, complete);
        }

        internal static bool Deliver(ItemDrop.ItemData item, MagicItem magic, List<SocketedEffect> removed,
            List<ItemDrop.ItemData> returned, Inventory inventory, Vector3? overflowPosition) =>
            DeliverAndComplete(item, magic, removed, returned, inventory, overflowPosition, null);

        private static bool DeliverAndComplete(ItemDrop.ItemData item, MagicItem magic, List<SocketedEffect> removed,
            List<ItemDrop.ItemData> returned, Inventory inventory, Vector3? overflowPosition, Action complete)
        {
            var items = inventory?.GetAllItems() ?? new List<ItemDrop.ItemData>();
            var savedItems = items.ToArray();
            var savedStacks = savedItems.Select(stored => stored.m_stack).ToArray();
            var savedData = savedItems.Select(stored => new Dictionary<string, string>(stored.m_customData)).ToArray();
            var savedCheated = savedItems.Select(stored => stored.m_cheated).ToArray();
            var savedSockets = magic.Sockets.ToArray();
            var savedEffects = savedSockets.Select(socket => socket?.Effect).ToArray();
            var savedMultipliers = savedSockets.Select(socket => socket?.StackMultiplier ?? 1f).ToArray();
            var savedRolled = magic.Effects.ToArray();
            var savedAugmented = magic.AugmentedEffectIndices.ToArray();
            var savedTempered = magic.TemperedEffectIndices.ToArray();
            int savedAugmentedIndex = magic.AugmentedEffectIndex;
            string savedName = magic.DisplayName;
            var changed = inventory == null ? null : GameFields.InventoryChanged(inventory);
            var drops = new List<ItemDrop>();
            bool accepted = false;
            if (inventory != null) GameFields.InventoryChanged(inventory) = null;
            try
            {
                // Reserve all returns through the real stack and slot rules, with no intermediate inventory publication.
                foreach (var stone in returned)
                    if (inventory == null || !inventory.CanAddItem(stone, 1) || !inventory.AddItem(stone))
                    {
                        if (!overflowPosition.HasValue)
                        {
                            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Make room for the returned shardstones or choose another effect.");
                            return false;
                        }
                        var drop = ItemDrop.DropItem(stone, 1, overflowPosition.Value, Quaternion.identity);
                        if (drop == null) return false;
                        drops.Add(drop);
                    }
                magic.Sockets.RemoveAll(removed.Contains);
                ShardSocketManager.RecomputeSocketValues(item, magic);
                SocketPositions.Retain(item, savedSockets, magic.Sockets);
                complete?.Invoke();
                accepted = true;
            }
            finally
            {
                if (!accepted)
                {
                    items.Clear();
                    items.AddRange(savedItems);
                    for (int saved = 0; saved < savedItems.Length; saved++)
                    {
                        savedItems[saved].m_stack = savedStacks[saved];
                        savedItems[saved].m_customData = savedData[saved];
                        savedItems[saved].m_cheated = savedCheated[saved];
                    }
                    magic.Sockets.Clear();
                    magic.Sockets.AddRange(savedSockets);
                    magic.Effects.Clear(); magic.Effects.AddRange(savedRolled);
                    magic.AugmentedEffectIndices.Clear(); magic.AugmentedEffectIndices.AddRange(savedAugmented);
                    magic.TemperedEffectIndices.Clear(); magic.TemperedEffectIndices.AddRange(savedTempered);
                    magic.AugmentedEffectIndex = savedAugmentedIndex;
                    magic.DisplayName = savedName;
                    for (int saved = 0; saved < savedSockets.Length; saved++)
                    {
                        if (savedSockets[saved] == null) continue;
                        savedSockets[saved].Effect = savedEffects[saved];
                        savedSockets[saved].StackMultiplier = savedMultipliers[saved];
                    }
                    foreach (var drop in drops) ZNetScene.instance.Destroy(drop.gameObject);
                    if (inventory != null) NotifyInventory(inventory, false, false);
                }
                if (inventory != null) GameFields.InventoryChanged(inventory) = changed;
            }
            return true;
        }

        internal static void NotifyReturns(Inventory inventory)
        {
            if (inventory != null) NotifyInventory(inventory, false, false);
        }
    }
}
