using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Shorten existing queued wearable equip and unequip delays to at most 0.8 seconds, with a 0.15 second pause.
    // Weapons, tools, ammunition, instant items, direct removal, reloads and equipment restored on login keep native timing.
    // Sprinting clears the action queue every frame, so a timed equip pressed mid-sprint never started. While an equip or
    // unequip is queued, the player doesn't sprint; with Run still held, sprinting resumes once the queue has no such action.
    // The game also lost equips in four other ways. An item pressed during an attack or cast was ignored; it now goes on or
    // off once the attack ends, in the order pressed. Staffs and weapons take 0.2 s to come out while tools come out at once,
    // so a tool pressed just after a staff was replaced by that staff; a tool now cancels hand items still queued before it.
    // A jump or dodge dropped every queued equip, and an equip finishing during a dodge failed; queued equips now carry on
    // through both and wait for the dodge to end.
    internal static class EquipmentTiming
    {
        private const float WearableSeconds = 0.8f, PauseSeconds = 0.15f, NativePauseSeconds = 0.3f;

        private static readonly AccessTools.FieldRef<Player, List<Player.MinorActionData>> ActionQueue =
            AccessTools.FieldRefAccess<Player, List<Player.MinorActionData>>("m_actionQueue");
        private static readonly AccessTools.FieldRef<Player, float> ActionPause = AccessTools.FieldRefAccess<Player, float>("m_actionQueuePause");
        private static readonly AccessTools.FieldRef<Player, string> ActionAnimation = AccessTools.FieldRefAccess<Player, string>("m_actionAnimation");
        private static readonly AccessTools.FieldRef<Character, ZSyncAnimation> Animation = AccessTools.FieldRefAccess<Character, ZSyncAnimation>("m_zanim");
        private static readonly MethodInfo ToggleMethod = AccessTools.Method(typeof(Player), "ToggleEquipped");
        private static readonly Func<Player, ItemDrop.ItemData, bool> Toggle = AccessTools.MethodDelegate<Func<Player, ItemDrop.ItemData, bool>>(ToggleMethod);
        // Player overrides Humanoid's ClearActionQueue; calls name the Humanoid declaration and dispatch to Player's.
        private static readonly MethodInfo ClearActionQueueMethod = AccessTools.Method(typeof(Humanoid), "ClearActionQueue");

        // Items pressed during the local player's attack, in order.
        private static readonly List<ItemDrop.ItemData> Pressed = new List<ItemDrop.ItemData>();

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(EquipmentTiming);
            harmony.Patch(AccessTools.Method(typeof(Player), "QueueEquipAction"), postfix: new HarmonyMethod(self, nameof(SetDuration)));
            harmony.Patch(AccessTools.Method(typeof(Player), "QueueUnequipAction"), postfix: new HarmonyMethod(self, nameof(SetDuration)));
            harmony.Patch(AccessTools.Method(typeof(Player), "UpdateActionQueue"),
                prefix: new HarmonyMethod(self, nameof(RememberAction)), postfix: new HarmonyMethod(self, nameof(ShortenPause)));
            harmony.Patch(AccessTools.Method(typeof(Player), "CheckRun"), prefix: new HarmonyMethod(self, nameof(PauseSprintForEquip)));
            harmony.Patch(ToggleMethod, prefix: new HarmonyMethod(self, nameof(HoldOrSupersede)));
            harmony.Patch(AccessTools.Method(typeof(Player), "OnJump"), transpiler: new HarmonyMethod(self, nameof(KeepThroughJump)));
            harmony.Patch(AccessTools.Method(typeof(Player), "UpdateDodge"), transpiler: new HarmonyMethod(self, nameof(KeepThroughDodge)));
        }

        private static bool IsEquipAction(Player.MinorActionData action) =>
            action.m_type == Player.MinorActionData.ActionType.Equip || action.m_type == Player.MinorActionData.ActionType.Unequip;

        private static bool PauseSprintForEquip(Player __instance, ref bool __result)
        {
            foreach (var action in ActionQueue(__instance))
                if (IsEquipAction(action)) { __result = false; return false; }
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

        private static bool IsHandItem(ItemDrop.ItemData item)
        {
            switch (item?.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon: case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft: case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Shield: case ItemDrop.ItemData.ItemType.Tool: case ItemDrop.ItemData.ItemType.Torch:
                    return true;
                default:
                    return false;
            }
        }

        // During an attack the game ignores the press. It is kept instead, and pressing the same item again takes it back.
        // An instant tool replaces whatever both hands hold, so hand items still queued before it would only replace it.
        private static bool HoldOrSupersede(Player __instance, ItemDrop.ItemData item, ref bool __result)
        {
            if (__instance != Player.m_localPlayer || item == null || !item.IsEquipable()) return true;
            if (__instance.InAttack() || (__instance.InDodge() && item.m_shared.m_equipDuration <= 0f))
            {
                if (!Pressed.Remove(item)) Pressed.Add(item);
                __result = true;
                return false;
            }
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool && item.m_shared.m_equipDuration <= 0f && !__instance.IsItemEquiped(item))
                ActionQueue(__instance).RemoveAll(action => action.m_type == Player.MinorActionData.ActionType.Equip && IsHandItem(action.m_item));
            return true;
        }

        private static void ApplyPressed(Player player)
        {
            if (Pressed.Count == 0 || player != Player.m_localPlayer || player.InAttack() || player.InDodge()) return;
            var items = Pressed.ToArray();
            Pressed.Clear();
            foreach (var item in items)
                if (player.GetInventory().ContainsItem(item)) Toggle(player, item);
        }

        private static void SetDuration(Player __instance, ItemDrop.ItemData item)
        {
            if (!IsWearable(item)) return;
            var queue = ActionQueue(__instance);
            for (int index = queue.Count - 1; index >= 0; index--)
                if (queue[index].m_item == item && IsEquipAction(queue[index]))
                { queue[index].m_duration = Mathf.Min(queue[index].m_duration, WearableSeconds); return; }
        }

        // Also applies items pressed during an attack, and holds a queued equip while the player dodges, as the game holds
        // the queue during an attack.
        private static bool RememberAction(Player __instance, out Player.MinorActionData __state)
        {
            __state = null;
            ApplyPressed(__instance);
            var queue = ActionQueue(__instance);
            if (ActionPause(__instance) <= 0f && queue.Count > 0 && IsEquipAction(queue[0]) && __instance.InDodge())
            {
                if (ActionAnimation(__instance) != null)
                {
                    Animation(__instance).SetBool(ActionAnimation(__instance), false);
                    ActionAnimation(__instance) = null;
                }
                return false;
            }
            __state = ActionPause(__instance) <= 0f && queue.Count > 0 ? queue[0] : null;
            return true;
        }

        private static void ShortenPause(Player __instance, Player.MinorActionData __state)
        {
            if (__state == null || __state.m_type == Player.MinorActionData.ActionType.Reload || !IsWearable(__state.m_item) || ActionQueue(__instance).Contains(__state)) return;
            if (Mathf.Approximately(ActionPause(__instance), NativePauseSeconds)) ActionPause(__instance) = PauseSeconds;
        }

        // A jump keeps queued equips and a staff's reload; crossbow reloads still stop as in the game. A dodge keeps queued
        // equips only.
        private static void ClearForJump(Player player) => ClearExcept(player, action => IsEquipAction(action) ||
            action.m_type == Player.MinorActionData.ActionType.Reload && StaffReload.Reloads(action.m_item));

        private static void ClearForDodge(Player player) => ClearExcept(player, IsEquipAction);

        private static void ClearExcept(Player player, Predicate<Player.MinorActionData> keep) => ActionQueue(player).RemoveAll(action => !keep(action));

        private static IEnumerable<CodeInstruction> KeepThroughJump(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(ClearActionQueueMethod),
                AccessTools.Method(typeof(EquipmentTiming), nameof(ClearForJump)), 1, "Player.OnJump changed; expected one action queue clear.");

        private static IEnumerable<CodeInstruction> KeepThroughDodge(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(ClearActionQueueMethod),
                AccessTools.Method(typeof(EquipmentTiming), nameof(ClearForDodge)), 1, "Player.UpdateDodge changed; expected one action queue clear.");
    }
}
