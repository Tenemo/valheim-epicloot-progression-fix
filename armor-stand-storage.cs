using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // An armor stand keeps one item in each native position and shows it on its model, but a trinket, and a utility item
    // without a belt model such as the Wishbone or Wisplight, has no position there. Each of the two gets an extra
    // position saved with the stand in the game's own item record: hotbar keys 1-8 fill it, E empties it together with
    // the native positions and a destroyed stand drops it. A belt shown natively and a utility item in the extra position
    // share one utility position, so neither replaces the other.
    internal static class ArmorStandStorage
    {
        internal const int Utility = 0, Trinket = 1;
        internal static readonly int[] ExtraKeys = { "epicloot_progression_stand_utility".GetStableHashCode(), "epicloot_progression_stand_trinket".GetStableHashCode() };
        private static readonly ItemDrop.ItemData.ItemType[] ExtraTypes = { ItemDrop.ItemData.ItemType.Utility, ItemDrop.ItemData.ItemType.Trinket };
        // A record holds its format, the item's prefab and the item at the newest item version, which the game's own
        // saves write.
        internal const byte Format = 1;
        private static readonly byte ItemVersion = (byte)Enum.GetValues(typeof(global::Version.Item)).Cast<int>().Max();
        private const float OwnershipSeconds = 3f;
        internal static readonly AccessTools.FieldRef<ArmorStand, ZNetView> View = AccessTools.FieldRefAccess<ArmorStand, ZNetView>("m_nview");
        private static int[] itemKeys = Array.Empty<int>(), variantKeys = Array.Empty<int>();
        private static ArmorStand pending;
        private static float pendingUntil;

        internal static int ItemKey(int index)
        {
            if (index >= itemKeys.Length)
            {
                itemKeys = Enumerable.Range(0, index + 8).Select(slot => (slot + "_item").GetStableHashCode()).ToArray();
                variantKeys = Enumerable.Range(0, index + 8).Select(slot => (slot + "_variant").GetStableHashCode()).ToArray();
            }
            return itemKeys[index];
        }

        // The armor stand whose attachment control this is, not its pose control.
        internal static ArmorStand Stand(Switch control)
        {
            var stand = control.GetComponentInParent<ArmorStand>();
            if (stand == null) return null;
            foreach (var slot in stand.m_slots) if (slot.m_switch == control) return stand;
            return null;
        }

        internal static bool Pending(ArmorStand stand) => pending == stand && Time.time < pendingUntil;

        // Runs the action once this game owns the stand, asking its owner as the stand's own controls do. One request
        // waits at a time; a stand that never answers leaves everything as it was.
        internal static void WhenOwner(ArmorStand stand, Player player, Action<ZNetView> action)
        {
            if (Time.time < pendingUntil) return;
            var view = View(stand);
            if (view.IsOwner()) { action(view); return; }
            view.InvokeRPC("RPC_RequestOwn");
            pending = stand;
            pendingUntil = Time.time + OwnershipSeconds;
            player.StartCoroutine(Wait(stand, view, player, action));
        }

        private static IEnumerator Wait(ArmorStand stand, ZNetView view, Player player, Action<ZNetView> action)
        {
            try
            {
                while (stand && view.IsValid() && !view.IsOwner() && Time.time < pendingUntil) yield return null;
                if (!stand || !view.IsValid() || player != Player.m_localPlayer) yield break;
                if (view.IsOwner()) action(view);
                else player.Message(MessageHud.MessageType.Center, "The armor stand did not respond.");
            }
            finally { pending = null; pendingUntil = 0f; }
        }

        // The native position that shows an item, chosen as the stand's own attachment chooses it, or -1. Chest and leg
        // armor always have a model; other items need the attach point the game looks for.
        internal static int NativeSlot(ArmorStand stand, ItemDrop.ItemData item)
        {
            var type = item.m_shared.m_itemType;
            var model = item.m_dropPrefab.transform;
            if (type != ItemDrop.ItemData.ItemType.Chest && type != ItemDrop.ItemData.ItemType.Legs && !model.Find("attach") && !model.Find("attach_skin")) return -1;
            var attach = item.m_shared.m_attachOverride != ItemDrop.ItemData.ItemType.None ? item.m_shared.m_attachOverride : type;
            for (int index = 0; index < stand.m_slots.Count; index++)
                if (stand.m_slots[index].m_supportedTypes.Count == 0 || stand.m_slots[index].m_supportedTypes.Contains(attach)) return index;
            return -1;
        }

        // The extra position of an item no native position shows, or -1.
        internal static int ExtraFor(ArmorStand stand, ItemDrop.ItemData item)
        {
            int extra = Array.IndexOf(ExtraTypes, item.m_shared.m_itemType);
            return extra >= 0 && NativeSlot(stand, item) < 0 ? extra : -1;
        }

        internal static ItemDrop.ItemData.ItemType ExtraType(int extra) => ExtraTypes[extra];

        // A native position's item, or null when it is empty or holds an item this game does not know.
        internal static ItemDrop.ItemData Native(ZDO zdo, int index, out bool unreadable)
        {
            int hash = zdo.GetInt(ItemKey(index));
            var prefab = hash == 0 ? null : ObjectDB.instance.GetItemPrefab(hash);
            unreadable = hash != 0 && prefab == null;
            if (prefab == null) return null;
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab;
            ItemDrop.LoadFromZDO(item, zdo, index);
            return item;
        }

        // The type of a native position's item without reading the item, or None.
        internal static ItemDrop.ItemData.ItemType NativeType(ZDO zdo, int index)
        {
            int hash = zdo.GetInt(ItemKey(index));
            var prefab = hash == 0 ? null : ObjectDB.instance.GetItemPrefab(hash);
            return prefab ? prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_itemType : ItemDrop.ItemData.ItemType.None;
        }

        internal static void SetNative(ZNetView view, int index, ItemDrop.ItemData item)
        {
            var zdo = view.GetZDO();
            int key = ItemKey(index);
            if (item == null)
            {
                zdo.Set(key, 0);
                view.InvokeRPC(ZNetView.Everybody, "RPC_SetVisualItem", index, 0, 0);
                return;
            }
            var stored = Unworn(item);
            int hash = item.m_dropPrefab.name.GetStableHashCode();
            zdo.Set(key, hash);
            zdo.Set(variantKeys[index], stored.m_variant);
            ItemDrop.SaveToZDO(stored, zdo, index);
            view.InvokeRPC(ZNetView.Everybody, "RPC_SetVisualItem", index, hash, stored.m_variant);
        }

        internal static bool HasExtra(ZDO zdo, int extra) => zdo.GetByteArray(ExtraKeys[extra])?.Length > 0;

        // An extra position's item, or null when it is empty or its record is unreadable; an unreadable record stays.
        internal static ItemDrop.ItemData Extra(ZDO zdo, int extra, out bool unreadable)
        {
            var data = zdo.GetByteArray(ExtraKeys[extra]);
            unreadable = false;
            if (data == null || data.Length == 0) return null;
            try
            {
                var package = new ZPackage(data);
                var prefab = package.ReadByte() == Format ? ObjectDB.instance.GetItemPrefab(package.ReadString()) : null;
                var item = prefab ? prefab.GetComponent<ItemDrop>().m_itemData.Clone() : null;
                if (item != null && item.m_shared.m_itemType == ExtraTypes[extra])
                {
                    item.m_dropPrefab = prefab;
                    var version = (global::Version.Item)package.ReadByte();
                    if (ItemDrop.ItemData.Load(package, item, version) == prefab.name.GetStableHashCode() && package.GetPos() == data.Length) return item;
                }
            }
            catch (EndOfStreamException) { }
            unreadable = true;
            return null;
        }

        internal static void SetExtra(ZDO zdo, int extra, ItemDrop.ItemData item)
        {
            if (item == null) { zdo.Set(ExtraKeys[extra], Array.Empty<byte>()); return; }
            var package = new ZPackage();
            package.Write(Format);
            package.Write(item.m_dropPrefab.name);
            package.Write(ItemVersion);
            Unworn(item).Save(package);
            zdo.Set(ExtraKeys[extra], package.GetArray());
        }

        private static ItemDrop.ItemData Unworn(ItemDrop.ItemData item)
        {
            var stored = item.Clone();
            stored.m_stack = 1;
            stored.m_equipped = false;
            return stored;
        }

        // Whether the shared utility position holds an item, natively or in the extra position.
        internal static bool UtilityTaken(ArmorStand stand, ZDO zdo)
        {
            if (HasExtra(zdo, Utility)) return true;
            for (int index = 0; index < stand.m_slots.Count; index++)
                if (NativeType(zdo, index) == ItemDrop.ItemData.ItemType.Utility) return true;
            return false;
        }

        // Ordinary retrieval and destruction drop the extra positions' items where the native ones drop.
        private static void DropExtras(ArmorStand stand)
        {
            var zdo = View(stand).GetZDO();
            for (int extra = 0; extra < ExtraKeys.Length; extra++)
            {
                var item = Extra(zdo, extra, out bool unreadable);
                if (unreadable) Debug.LogWarning("[EpicLoot Progression Fix] An armor stand keeps an unreadable " + ExtraTypes[extra] + " record at " + stand.transform.position + ".");
                if (item == null) continue;
                var drop = ItemDrop.DropItem(item, 1, stand.m_dropSpawnPoint.position, stand.m_dropSpawnPoint.rotation);
                drop.GetComponent<Rigidbody>().linearVelocity = Vector3.up * 4f;
                stand.m_destroyEffects.Create(stand.m_dropSpawnPoint.position, Quaternion.identity);
                SetExtra(zdo, extra, null);
            }
        }

        // Hotbar keys 1-8 store an item without a native position in its extra position, and E also empties the extra
        // positions when they are all the stand holds. A waiting exchange keeps both away from the stand.
        [HarmonyPatch(typeof(ArmorStand), "UseItem")]
        private static class Attach
        {
            private static bool Prefix(ArmorStand __instance, Switch caller, Humanoid user, ItemDrop.ItemData item, ref bool __result)
            {
                var view = View(__instance);
                if (!view.IsValid() || !PrivateArea.CheckAccess(__instance.transform.position, 0f, false)) return true;
                if (Pending(__instance)) { __result = true; return false; }
                var zdo = view.GetZDO();
                if (item == null)
                {
                    if (__instance.GetNrOfAttachedItems() > 0 || !HasExtra(zdo, Utility) && !HasExtra(zdo, Trinket)) return true;
                    view.InvokeRPC("RPC_DropItemByName", caller.name);
                    __result = true;
                    return false;
                }
                int extra = ExtraFor(__instance, item);
                if (extra < 0)
                {
                    // A belt cannot join a utility item already in the extra position.
                    if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Utility || !HasExtra(zdo, Utility)) return true;
                    __result = false;
                    return false;
                }
                // Like a taken native position, a taken extra position leaves the key to its ordinary use.
                __result = user is Player && !(extra == Utility ? UtilityTaken(__instance, zdo) : HasExtra(zdo, extra));
                if (__result) WhenOwner(__instance, (Player)user, owned => Store(__instance, owned, (Player)user, item, extra));
                return false;
            }

            private static void Store(ArmorStand stand, ZNetView view, Player player, ItemDrop.ItemData item, int extra)
            {
                var zdo = view.GetZDO();
                if (!player.GetInventory().ContainsItem(item) || (extra == Utility ? UtilityTaken(stand, zdo) : HasExtra(zdo, extra))) return;
                SetExtra(zdo, extra, item);
                player.UnequipItem(item);
                player.GetInventory().RemoveOneItem(item);
                stand.m_effects.Create(stand.transform.position, Quaternion.identity);
                Game.instance.IncrementPlayerStat(PlayerStatType.ArmorStandUses);
            }
        }

        [HarmonyPatch(typeof(ArmorStand), "RPC_DropItemByName")]
        private static class Retrieve
        {
            private static void Postfix(ArmorStand __instance, string name)
            {
                if (!View(__instance).IsOwner()) return;
                foreach (var slot in __instance.m_slots)
                    if (slot.m_switch.name == name) { DropExtras(__instance); return; }
            }
        }

        [HarmonyPatch(typeof(ArmorStand), "OnDestroyed")]
        private static class Destroyed
        {
            private static void Postfix(ArmorStand __instance)
            {
                if (View(__instance).IsOwner()) DropExtras(__instance);
            }
        }
    }
}
