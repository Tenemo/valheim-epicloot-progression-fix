using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using EpicLoot;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EquipmentAndQuickSlots predicts native belt and Corpse Run capacity, but not EpicLoot's
    // carry enchantments on the armor and accessories it will re-equip. Credit those for the
    // existing fit check only; the actual transfer and equipping remain with the original mods.
    internal static class GraveCarryWeight
    {
        private static Func<Player, ItemDrop.ItemData, string, bool> willEquip;
        private static Func<ItemDrop.ItemData, object> slotOf;
        private static PropertyInfo slotId;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.EquipmentAndQuickSlots, out var slots)) return;
            var pickup = slots.Instance.GetType().Assembly.GetType("EquipmentAndQuickSlots.DeathPatches", true);
            var eligibility = AccessTools.Method(pickup, "IsItemToEquip");
            var slotType = slots.Instance.GetType().Assembly.GetType("EquipmentAndQuickSlots.Slots", true);
            var itemSlot = AccessTools.Method(slotType, "GetItemSlot");
            slotId = slotType.GetNestedType("Slot").GetProperty("ID");
            if (eligibility == null || itemSlot == null || slotId == null)
                throw new InvalidOperationException("EquipmentAndQuickSlots' grave auto-equip check moved.");
            willEquip = AccessTools.MethodDelegate<Func<Player, ItemDrop.ItemData, string, bool>>(eligibility);
            slotOf = AccessTools.MethodDelegate<Func<ItemDrop.ItemData, object>>(itemSlot);
            harmony.Patch(AccessTools.Method(typeof(TombStone), "EasyFitInInventory"),
                prefix: new HarmonyMethod(typeof(GraveCarryWeight), nameof(Prefix)),
                finalizer: new HarmonyMethod(typeof(GraveCarryWeight), nameof(Finalizer)));
            log.LogInfo("Grave recovery counts EpicLoot carry enchantments on returned armor and accessories.");
        }

        [HarmonyPriority(Priority.First)]
        private static void Prefix(Player player, Container ___m_container, out float __state)
        {
            __state = 0f;
            if (player != Player.m_localPlayer || player == null || player.IsDead()
                || player.InAttack() || player.InDodge() || player.IsSwimming() && !player.IsOnGround()) return;

            string playerId = Game.instance.GetPlayerProfile().GetPlayerID().ToString();
            var returned = new Dictionary<string, ItemDrop.ItemData>();
            foreach (var item in ___m_container.GetInventory().GetAllItems().OrderBy(item => item.m_gridPos.y).ThenBy(item => item.m_gridPos.x))
            {
                if (!IsArmorOrAccessory(item.m_shared.m_itemType) || !willEquip(player, item, playerId)) continue;
                if (item.m_shared.m_useDurability && item.m_durability <= 0f
                    && item.GetMagicItem()?.HasEffect(MagicEffectType.Indestructible) != true) continue;
                if (item.m_shared.m_dlc.Length > 0 && !DLCMan.instance.IsDLCInstalled(item.m_shared.m_dlc)) continue;
                if (Game.m_worldLevel > 0 && item.m_worldLevel < Game.m_worldLevel
                    && (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Utility || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trinket)) continue;
                returned[EquipmentSlot(item, true)] = item;
            }
            float bonus = 0f;
            foreach (var item in returned.Values) bonus += CarryBonus(item);
            foreach (var item in player.GetInventory().GetAllItems())
                if (returned.ContainsKey(EquipmentSlot(item, false)) && player.IsItemEquiped(item)) bonus -= CarryBonus(item);
            player.m_maxCarryWeight += bonus;
            __state = bonus;
        }

        [HarmonyPriority(Priority.Last)]
        private static void Finalizer(Player player, float __state)
        {
            if (player != null) player.m_maxCarryWeight -= __state;
        }

        private static float CarryBonus(ItemDrop.ItemData item) =>
            item.GetMagicItem()?.GetTotalEffectValue(MagicEffectType.AddCarryWeight) ?? 0f;

        private static string EquipmentSlot(ItemDrop.ItemData item, bool returning)
        {
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Utility)
            {
                if (returning && item.m_customData.TryGetValue("eaqs_slot", out string remembered)
                    && remembered.StartsWith("Utility", StringComparison.Ordinal)) return remembered;
                var slot = slotOf(item);
                if (slot != null) return (string)slotId.GetValue(slot);
            }
            return item.m_shared.m_itemType.ToString();
        }

        private static bool IsArmorOrAccessory(ItemDrop.ItemData.ItemType type) =>
            type == ItemDrop.ItemData.ItemType.Helmet || type == ItemDrop.ItemData.ItemType.Chest
            || type == ItemDrop.ItemData.ItemType.Legs || type == ItemDrop.ItemData.ItemType.Shoulder
            || type == ItemDrop.ItemData.ItemType.Utility || type == ItemDrop.ItemData.ItemType.Trinket;
    }
}
