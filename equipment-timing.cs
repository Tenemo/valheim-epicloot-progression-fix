using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Shorten existing queued wearable equip and unequip delays to at most 0.8 seconds, with a 0.15 second pause.
    // Weapons, tools, ammunition, instant items, direct removal, reloads and equipment restored on login keep native timing.
    // Sprinting clears the action queue every frame, so a timed equip pressed mid-sprint never started. While an equip or
    // unequip is queued, the player doesn't sprint; with Run still held, sprinting resumes once the queue has no such action.
    internal static class EquipmentTiming
    {
        private const float WearableSeconds = 0.8f, PauseSeconds = 0.15f, NativePauseSeconds = 0.3f;

        private static readonly AccessTools.FieldRef<Player, List<Player.MinorActionData>> ActionQueue =
            AccessTools.FieldRefAccess<Player, List<Player.MinorActionData>>("m_actionQueue");
        private static readonly AccessTools.FieldRef<Player, float> ActionPause = AccessTools.FieldRefAccess<Player, float>("m_actionQueuePause");
        internal static void Initialize(IntegrationPatches harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Player), "QueueEquipAction"), postfix: new HarmonyMethod(typeof(EquipmentTiming), nameof(SetDuration)));
            harmony.Patch(AccessTools.Method(typeof(Player), "QueueUnequipAction"), postfix: new HarmonyMethod(typeof(EquipmentTiming), nameof(SetDuration)));
            harmony.Patch(AccessTools.Method(typeof(Player), "UpdateActionQueue"),
                prefix: new HarmonyMethod(typeof(EquipmentTiming), nameof(RememberAction)), postfix: new HarmonyMethod(typeof(EquipmentTiming), nameof(ShortenPause)));
            harmony.Patch(AccessTools.Method(typeof(Player), "CheckRun"), prefix: new HarmonyMethod(typeof(EquipmentTiming), nameof(PauseSprintForEquip)));
        }

        private static bool PauseSprintForEquip(Player __instance, ref bool __result)
        {
            foreach (var action in ActionQueue(__instance))
                if (action.m_type == Player.MinorActionData.ActionType.Equip || action.m_type == Player.MinorActionData.ActionType.Unequip)
                { __result = false; return false; }
            return true;
        }

        private static bool IsWearable(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            var type = item.m_shared.m_itemType;
            return type == ItemDrop.ItemData.ItemType.Helmet || type == ItemDrop.ItemData.ItemType.Chest
                || type == ItemDrop.ItemData.ItemType.Legs || type == ItemDrop.ItemData.ItemType.Shoulder
                || type == ItemDrop.ItemData.ItemType.Utility || type == ItemDrop.ItemData.ItemType.Trinket;
        }

        private static void SetDuration(Player __instance, ItemDrop.ItemData item)
        {
            if (!IsWearable(item)) return;
            var queue = ActionQueue(__instance);
            for (int index = queue.Count - 1; index >= 0; index--)
                if (queue[index].m_item == item && (queue[index].m_type == Player.MinorActionData.ActionType.Equip ||
                    queue[index].m_type == Player.MinorActionData.ActionType.Unequip))
                { queue[index].m_duration = Mathf.Min(queue[index].m_duration, WearableSeconds); return; }
        }

        private static void RememberAction(Player __instance, out Player.MinorActionData __state)
        {
            var queue = ActionQueue(__instance);
            __state = ActionPause(__instance) <= 0f && queue.Count > 0 ? queue[0] : null;
        }

        private static void ShortenPause(Player __instance, Player.MinorActionData __state)
        {
            if (__state == null || __state.m_type == Player.MinorActionData.ActionType.Reload || !IsWearable(__state.m_item) || ActionQueue(__instance).Contains(__state)) return;
            if (Mathf.Approximately(ActionPause(__instance), NativePauseSeconds)) ActionPause(__instance) = PauseSeconds;
        }
    }
}
