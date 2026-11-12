using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A recovered weapon is saved with the character outside the inventory the tombstone empties. Its owned
    // projectile or drop is removed first; only then can the living character receive that original item once.
    internal static class RecallRecovery
    {
        internal const string StateKey = Plugin.Id + ".recall-recovery";
        private const string ItemKey = Plugin.Id + ".recall-item";
        // The inventory cell a weapon was thrown from. Recovery saves rewrite grid positions, so it travels in the item's data.
        private const string CellKey = Plugin.Id + ".recall-cell";
        private sealed class Source
        {
            internal Player Player;
            internal ItemDrop.ItemData Item;
        }
        private static readonly Dictionary<GameObject, Source> Sources = new();
        private static readonly Dictionary<long, List<ItemDrop.ItemData>> Recovered = new();
        // The local character's cells kept empty for its thrown weapons, by weapon identity, until each returns. Death,
        // logout and a new connection release them; a pending weapon still takes its saved cell back if that is free.
        private static readonly Dictionary<string, Vector2i> Reserved = new();
        private static Player loadedPlayer;
        private static readonly Action<ItemDrop> SaveDrop =
            AccessTools.MethodDelegate<Action<ItemDrop>>(AccessTools.Method(typeof(ItemDrop), "Save"));
        private static bool showedNoRoom;
        internal static string Problem { get; private set; }

        internal static void Initialize(IntegrationPatches harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Projectile), nameof(Projectile.Setup)),
                postfix: new HarmonyMethod(typeof(RecallRecovery), nameof(TrackProjectile)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.OnDeath)),
                prefix: new HarmonyMethod(typeof(RecallRecovery), nameof(BeforeDeath)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(Game), "Shutdown"),
                prefix: new HarmonyMethod(typeof(RecallRecovery), nameof(BeforeLogout)));
            harmony.Patch(AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.Destroy)),
                prefix: new HarmonyMethod(typeof(RecallRecovery), nameof(BeforeDestroy)));
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.Save)),
                prefix: new HarmonyMethod(typeof(RecallRecovery), nameof(BeforeSave)));
        }

        internal static void Forget()
        {
            Sources.Clear(); Recovered.Clear(); Reserved.Clear(); loadedPlayer = null; Problem = null;
        }

        // Whether one of this local character's recalling weapons is still a projectile or a drop on its way back.
        internal static bool Outstanding(Player player)
        {
            foreach (var entry in Sources)
                if (entry.Key && entry.Value.Player == player) return true;
            return false;
        }

        private static string Identity(ItemDrop.ItemData item) => item.m_customData[ItemKey];
        private static bool Contains(Inventory inventory, ItemDrop.ItemData item) => inventory.GetAllItems().Any(other =>
            other.m_customData.TryGetValue(ItemKey, out string id) && id == Identity(item));

        private static void TrackProjectile(Projectile __instance, Character owner)
        {
            var item = __instance.m_spawnItem;
            if (owner != Player.m_localPlayer || !(owner is Player player) || item == null ||
                !item.HasMagicEffect(MagicEffectType.RecallWeapon)) return;
            if (player.GetInventory().ContainsItem(item))
            {
                // A fresh throw: Attack consumes the weapon after this setup, so its cell is still known here.
                if (item.m_customData.TryGetValue(ItemKey, out string previous)) Reserved.Remove(previous);
                item.m_customData[ItemKey] = Guid.NewGuid().ToString("N");
                item.m_customData[CellKey] = item.m_gridPos.x + "," + item.m_gridPos.y;
                Reserved[Identity(item)] = item.m_gridPos;
            }
            else if (!item.m_customData.ContainsKey(ItemKey)) item.m_customData[ItemKey] = Guid.NewGuid().ToString("N");
            Sources[__instance.gameObject] = new Source { Player = player, Item = item };
        }

        private static bool Local(Inventory inventory)
        {
            var player = Player.m_localPlayer;
            return inventory != null && player != null && player.GetInventory() == inventory;
        }

        // Whether this free cell of the local character's inventory is kept for a thrown weapon other than the given item.
        internal static bool Reserves(Inventory inventory, Vector2i cell, ItemDrop.ItemData item = null)
        {
            if (Reserved.Count == 0 || !Local(inventory)) return false;
            string own = item != null && item.m_customData.TryGetValue(ItemKey, out string id) ? id : null;
            return Reserved.Any(entry => entry.Value == cell && entry.Key != own) && inventory.GetItemAt(cell.x, cell.y) == null;
        }

        // Kept cells that are free within the first rows, where native and EquipmentAndQuickSlots counts see empty cells.
        internal static int ReservedFreeCells(Inventory inventory, int rows) => Reserved.Count == 0 || !Local(inventory) ? 0 :
            Reserved.Values.Distinct().Count(cell => cell.y < rows && cell.x < inventory.GetWidth() && inventory.GetItemAt(cell.x, cell.y) == null);

        // A returning weapon's saved cell, while it is inside the local inventory, free and not kept for another weapon.
        internal static bool PreferredCell(Inventory inventory, ItemDrop.ItemData item, out Vector2i cell)
        {
            cell = new Vector2i(-1, -1);
            if (item == null || !item.m_customData.TryGetValue(CellKey, out string saved) || !Local(inventory)) return false;
            string[] parts = saved.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)) return false;
            cell = new Vector2i(x, y);
            return x >= 0 && y >= 0 && x < inventory.GetWidth() && y < inventory.GetHeight() &&
                inventory.GetItemAt(x, y) == null && !Reserves(inventory, cell, item);
        }

        // An item entered the local inventory; if it is a returning weapon, forget its cell.
        internal static void Returned(Inventory inventory, ItemDrop.ItemData item)
        {
            if (!Local(inventory)) return;
            item.m_customData.Remove(CellKey);
            if (item.m_customData.TryGetValue(ItemKey, out string id)) Reserved.Remove(id);
        }

        internal static void Landed(ItemDrop drop, Projectile projectile, Player player)
        {
            Sources.Remove(projectile.gameObject);
            // SpawnOnHit keeps this reference after creating its drop; clear it so projectile expiry cannot
            // recover a second copy. The drop now carries the original item through the usual pickup path.
            projectile.m_spawnItem = null;
            TrackDrop(drop, player);
        }

        internal static void TrackDrop(ItemDrop drop, Player player)
        {
            if (player != Player.m_localPlayer) return;
            if (!drop.m_itemData.m_customData.ContainsKey(ItemKey))
            {
                drop.m_itemData.m_customData[ItemKey] = Guid.NewGuid().ToString("N");
                SaveDrop(drop);
            }
            Sources[drop.gameObject] = new Source { Player = player, Item = drop.m_itemData };
        }

        private static void BeforeDeath(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            Capture(__instance, dying: true);
            Reserved.Clear();
        }

        private static void BeforeLogout()
        {
            var player = Player.m_localPlayer;
            if (player) Capture(player);
            Reserved.Clear();
        }

        private static void Capture(Player player, bool dying = false)
        {
            Load(player);
            if (Problem != null) return;
            foreach (var entry in Sources.Where(entry => entry.Value.Player == player).ToArray())
            {
                var view = entry.Key ? entry.Key.GetComponent<ZNetView>() : null;
                if (!view || !view.IsValid() || !view.IsOwner()) continue;
                // Attack consumes its weapon after setting up the projectile. Death from that same attack
                // can occur between those two calls, while the original weapon is still in the inventory.
                if (player.GetInventory().ContainsItem(entry.Value.Item))
                {
                    if (!dying) continue;
                    player.UnequipItem(entry.Value.Item, triggerEquipEffects: false);
                    player.GetInventory().RemoveItem(entry.Value.Item);
                }
                Take(entry.Key, entry.Value.Item, player);
            }
        }

        private static void BeforeDestroy(GameObject go)
        {
            if (!Sources.TryGetValue(go, out var source)) return;
            // Normal pickup has already inserted the weapon. Expiry without an impact has not, and must
            // preserve it before the game's projectile destruction can discard m_spawnItem.
            Take(go, source.Item, source.Player, destroy: false);
            Sources.Remove(go);
        }

        internal static bool Take(GameObject source, ItemDrop.ItemData item, Player player, bool destroy = true)
        {
            if (!source || !player || player.GetPlayerID() != Player.m_localPlayer?.GetPlayerID()) return false;
            var view = source.GetComponent<ZNetView>();
            if (!view || !view.IsValid() || !view.IsOwner()) return false;
            Load(player);
            if (Problem != null) return false;
            if (!item.m_customData.ContainsKey(ItemKey)) return false;
            var held = Recovered[player.GetPlayerID()];
            if (!Contains(player.GetInventory(), item) && !held.Any(other => Identity(other) == Identity(item)))
            {
                var saved = item.Clone();
                saved.m_equipped = false;
                saved.m_lastProjectile = null;
                held.Add(saved);
                Write(player);
            }
            Sources.Remove(source);
            var projectile = source.GetComponent<Projectile>();
            if (projectile) projectile.m_spawnItem = null;
            if (destroy) ZNetScene.instance.Destroy(source);
            return true;
        }

        private static void Load(Player player)
        {
            if (loadedPlayer == player) return;
            loadedPlayer = player; showedNoRoom = false; Problem = null;
            long id = player.GetPlayerID();
            if (!Recovered.TryGetValue(id, out var held)) Recovered[id] = held = new();
            if (!player.m_customData.TryGetValue(StateKey, out string state)) return;
            try
            {
                var package = new ZPackage(Convert.FromBase64String(state));
                package.ReadInt();
                int count = package.ReadUShort();
                package.SetPos(0);
                var inventory = new Inventory("Recalling weapons", null, 8, Math.Max(1, (count + 7) / 8));
                inventory.Load(package);
                if (inventory.NrOfItems() != count || package.GetPos() != package.Size())
                    throw new InvalidDataException("A saved recalling weapon could not be loaded.");
                foreach (var item in inventory.GetAllItems())
                    if (!held.Any(other => Identity(other) == Identity(item))) held.Add(item);
            }
            catch (Exception error)
            {
                Problem = "Recalling weapons: " + error.GetBaseException().Message;
                Debug.LogError(Problem + " The character's saved recovery data was left unchanged.");
            }
        }

        private static void Write(Player player)
        {
            if (Problem != null || !Recovered.TryGetValue(player.GetPlayerID(), out var held)) return;
            if (held.Count == 0) { player.m_customData.Remove(StateKey); return; }
            var package = new ZPackage();
            package.Write(109); package.Write((ushort)held.Count);
            for (int index = 0; index < held.Count; index++)
            {
                var item = held[index].Clone();
                item.m_gridPos = new Vector2i(index % 8, index / 8);
                item.Save(package);
            }
            player.m_customData[StateKey] = Convert.ToBase64String(package.GetArray());
        }

        private static void BeforeSave(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            Capture(__instance); Load(__instance); Write(__instance);
        }

        internal static bool AlreadyCarried(Player player, ItemDrop.ItemData item) =>
            item.m_customData.ContainsKey(ItemKey) && Contains(player.GetInventory(), item);

        internal static void Tick()
        {
            var player = Player.m_localPlayer;
            if (!player || player.GetPlayerID() == 0) return;
            // Release the cell of a weapon that is carried again by any route, such as an attack that did not consume it.
            if (Reserved.Count > 0)
                foreach (var item in player.GetInventory().GetAllItems())
                    if (item.m_customData.TryGetValue(ItemKey, out string id) && Reserved.ContainsKey(id)) Returned(player.GetInventory(), item);
            Load(player);
            if (Problem != null || player.IsDead() || Game.instance == null || Game.instance.WaitingForRespawn()) return;
            var held = Recovered[player.GetPlayerID()];
            if (held.Count == 0) return;
            foreach (var item in held.ToArray())
            {
                if (Contains(player.GetInventory(), item)) { held.Remove(item); Write(player); continue; }
                if (!player.GetInventory().CanAddItem(item))
                {
                    if (!showedNoRoom) { player.Message(MessageHud.MessageType.Center, "$msg_noroom"); showedNoRoom = true; }
                    continue;
                }
                if (!player.GetInventory().AddItem(item)) continue;
                held.Remove(item); Write(player); showedNoRoom = false;
                player.StartCoroutine(RecallPriority.EquipReturnedWeapon(player, item));
            }
        }
    }
}
