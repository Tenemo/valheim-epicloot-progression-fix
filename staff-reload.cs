using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Dundr pays for its shots by draining eitr while it reloads, which the game reads straight from the staff, so attack
    // eitr cost never lowered it and fire rate sped up only the shot. Attack eitr cost from the staff, worn gear and sets
    // now lowers the reload's drain and the eitr needed to start it, as it lowers other staves' cast cost, and the staff's
    // fire rate also shortens the reload while it drains the same eitr per reload. Attack speed still speeds up only the shot.
    [HarmonyPatch]
    internal static class StaffReload
    {
        private static bool Reloads(ItemDrop.ItemData weapon) =>
            weapon?.m_shared?.m_attack != null && weapon.m_shared.m_attack.m_requiresReload && FireRate.EffectFor(weapon) == MagicEffectType.ModifyMagicFireRate;

        internal static float FireRateFactor(Player player, ItemDrop.ItemData weapon) =>
            player && Reloads(weapon) ? 1f + Mathf.Max(0f, player.GetTotalActiveMagicEffectValue(MagicEffectType.ModifyMagicFireRate, .01f)) : 1f;

        private static float EitrFactor(Player player, ItemDrop.ItemData weapon) =>
            player ? Mathf.Max(0f, ModifyAttackCosts.GetModifyAttackValue(player, weapon, MagicEffectType.ModifyAttackEitrUse)) : 1f;

        // The eitr a reload drains each second, before the game's eitr rate.
        internal static float DrainPerSecond(Player player, ItemDrop.ItemData weapon) =>
            weapon.m_shared.m_attack.m_reloadEitrDrain * EitrFactor(player, weapon) * FireRateFactor(player, weapon);

        // The game keeps one loaded weapon per player and forgets it whenever a weapon leaves the hands, so switching slots,
        // hiding weapons or eating threw Dundr's shot away and taking it out again started a new reload. A loaded staff now
        // stays loaded while it is away, until it fires. Crossbows keep the game's behavior.
        private static readonly AccessTools.FieldRef<Player, ItemDrop.ItemData> LoadedWeapon = AccessTools.FieldRefAccess<Player, ItemDrop.ItemData>("m_weaponLoaded");
        private static readonly Action<Player, ItemDrop.ItemData> SetWeaponLoaded =
            AccessTools.MethodDelegate<Action<Player, ItemDrop.ItemData>>(AccessTools.Method(typeof(Player), "SetWeaponLoaded"));
        private static readonly ConditionalWeakTable<ItemDrop.ItemData, object> Stowed = new ConditionalWeakTable<ItemDrop.ItemData, object>();

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem)), HarmonyPrefix]
        private static void Stow(Humanoid __instance, ItemDrop.ItemData item)
        {
            if (item == null || !(__instance is Player player) || player != Player.m_localPlayer || LoadedWeapon(player) != item || !Reloads(item)) return;
            Stowed.Remove(item);
            Stowed.Add(item, null);
        }

        [HarmonyPatch(typeof(Player), "UpdateWeaponLoading"), HarmonyPrefix]
        private static void TakeOut(Player __instance, ItemDrop.ItemData weapon)
        {
            if (weapon == null || LoadedWeapon(__instance) == weapon || !Stowed.TryGetValue(weapon, out _)) return;
            Stowed.Remove(weapon);
            SetWeaponLoaded(__instance, weapon);
        }

        // The game clears every queued action when you jump, ground or air jump, so a jump threw away the reload's progress and
        // the eitr it had drained, and the reload started over. A staff's reload now carries on through a jump; queued
        // equipping and crossbow reloads still stop as in the game.
        private static readonly AccessTools.FieldRef<Player, List<Player.MinorActionData>> ActionQueue =
            AccessTools.FieldRefAccess<Player, List<Player.MinorActionData>>("m_actionQueue");
        // Player overrides Humanoid's ClearActionQueue; calls name the Humanoid declaration and dispatch to Player's.
        private static readonly MethodInfo ClearActionQueueMethod = AccessTools.Method(typeof(Humanoid), "ClearActionQueue");
        private static readonly Action<Humanoid> ClearActionQueue = AccessTools.MethodDelegate<Action<Humanoid>>(ClearActionQueueMethod);

        private static void ClearForJump(Player player)
        {
            var reload = ActionQueue(player).Find(action => action.m_type == Player.MinorActionData.ActionType.Reload && Reloads(action.m_item));
            ClearActionQueue(player);
            if (reload != null) ActionQueue(player).Add(reload);
        }

        [HarmonyPatch(typeof(Player), "OnJump"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> KeepThroughJump(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(ClearActionQueueMethod),
                AccessTools.Method(typeof(StaffReload), nameof(ClearForJump)), 1, "Player.OnJump changed; expected one action queue clear.");

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetWeaponLoadingTime)), HarmonyPostfix]
        private static void LoadingTime(ItemDrop.ItemData __instance, ref float __result) => __result /= FireRateFactor(Player.m_localPlayer, __instance);

        private static float StartEitr(Attack attack, Player player) =>
            Own(attack, player, out var weapon) ? attack.m_reloadEitrDrain * EitrFactor(player, weapon) : attack.m_reloadEitrDrain;

        private static float QueuedDrain(Attack attack, Player player) =>
            Own(attack, player, out var weapon) ? DrainPerSecond(player, weapon) : attack.m_reloadEitrDrain;

        private static bool Own(Attack attack, Player player, out ItemDrop.ItemData weapon)
        {
            weapon = player == Player.m_localPlayer && player ? player.GetCurrentWeapon() : null;
            return weapon?.m_shared?.m_attack == attack;
        }

        [HarmonyPatch(typeof(Player), "UpdateWeaponLoading"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> StartCost(IEnumerable<CodeInstruction> instructions) =>
            Replace(instructions, nameof(StartEitr), "Dundr's reload start check changed.");

        [HarmonyPatch(typeof(Player), "QueueReloadAction"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Drain(IEnumerable<CodeInstruction> instructions) =>
            Replace(instructions, nameof(QueuedDrain), "Dundr's reload drain changed.");

        // Reads the reload drain through the helper, with the player as its second argument.
        private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, string helper, string failure)
        {
            var drain = AccessTools.Field(typeof(Attack), nameof(Attack.m_reloadEitrDrain));
            var result = new List<CodeInstruction>();
            int changes = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.LoadsField(drain))
                {
                    result.Add(new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction));
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(StaffReload), helper)));
                    changes++;
                    continue;
                }
                result.Add(instruction);
            }
            if (changes != 1) throw new InvalidOperationException(failure);
            return result;
        }
    }
}
