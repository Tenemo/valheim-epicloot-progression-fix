using System;
using EpicLoot;
using EpicLoot.Magic.MagicItemEffects;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Triple Shot's removal delegate knows the complete ammunition cost. Conserve each paid arrow here,
    // rather than refunding one prefab item after the volley; retained ammunition keeps its item data.
    internal static class AmmoConservationPerArrow
    {
        private static ItemDrop.ItemData conservedAmmo;
        private static bool conservedLoadedArrow;
        private static readonly AccessTools.FieldRef<bool> SkipReload = AccessTools.StaticFieldRefAccess<bool>(
            AccessTools.Field(typeof(AmmoConservation), "skipReload"));

        [HarmonyPatch(typeof(UseAmmoTranspilerPatch), nameof(UseAmmoTranspilerPatch.CustomRemoveItem))]
        private static class SpendAmmunition
        {
            private static bool Prefix(Inventory inventory, ItemDrop.ItemData item, int amount, ref bool __result)
            {
                conservedAmmo = null;
                conservedLoadedArrow = false;
                var player = Player.m_localPlayer;
                if (player == null || inventory != player.GetInventory() || item == null) return true;
                float chance = Mathf.Clamp01(player.GetTotalActiveMagicEffectValue(MagicEffectType.AmmoConservation, 0.01f));
                if (chance <= 0f) return true;

                if (MultiShot.IsTripleShotActive)
                {
                    amount *= MultiShot.ShotProjectiles;
                    MultiShot.IsTripleShotActive = false;
                    MultiShot.ShotProjectiles = 0;
                }
                // Native removal cannot spend more than the stack contains, even for a short volley.
                int paid = Math.Min(amount, item.m_stack);
                int consumed = 0;
                conservedAmmo = item;
                for (int arrow = 0; arrow < paid; arrow++)
                {
                    bool saved = UnityEngine.Random.value < chance;
                    if (arrow == 0) conservedLoadedArrow = saved;
                    if (!saved) consumed++;
                }
                __result = consumed == 0 || inventory.RemoveItem(item, consumed);
                return false;
            }
        }

        [HarmonyPatch(typeof(AmmoConservation.AmmoConservation_Attack_UseAmmo_Patch), nameof(AmmoConservation.AmmoConservation_Attack_UseAmmo_Patch.Postfix))]
        private static class FinishConservation
        {
            private static bool Prefix(Attack __0, bool __1, ItemDrop.ItemData __2)
            {
                var player = Player.m_localPlayer;
                bool handled = player != null && GameFields.AttackCharacter(__0) == player && __2 != null && __2 == conservedAmmo;
                if (__1 && handled && conservedLoadedArrow)
                {
                    var weapon = __0.GetWeapon();
                    if (weapon != null && weapon.m_shared.m_attack.m_requiresReload) SkipReload() = true;
                }
                conservedAmmo = null;
                conservedLoadedArrow = false;
                // The spending step already applied conservation. Keep the crossbow's existing loaded-bolt
                // reload skip, without a second conservation roll or an inventory refund.
                return !handled;
            }
        }
    }
}
