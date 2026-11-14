using System;
using EpicLoot.Transformation;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The Jotun Witch form fights with the creature's own attacks, which dealt the same damage on a first staff as on a fully
    // upgraded and enchanted one, about a tenth of a staff's. Her dodge was an attack that left her open to the hit she dodged,
    // she blocked with that attack's own 12.7 block power instead of her staff's, and casting in the air held her in place.
    // Her blast and bolt now grow with the staff in her hand: they deal BlastFactor and BoltFactor times their base damage,
    // multiplied by how far upgrades and damage enchantments have raised that staff's damage above its base. She blocks and
    // parries with the staff, her dodge is invulnerable for as long as a roll is, and she flies at half speed while casting.
    internal static class HexenFormCombat
    {
        internal const float BlastFactor = 3f, BoltFactor = 2.4f, DodgeInvulnerableSeconds = .5f, CastingFlightSpeed = .5f;
        private static readonly AccessTools.FieldRef<HexenForm, Player> FormPlayer = AccessTools.FieldRefAccess<HexenForm, Player>("_player");
        private static readonly AccessTools.FieldRef<HexenForm, ItemDrop.ItemData> Blast = AccessTools.FieldRefAccess<HexenForm, ItemDrop.ItemData>("_blast");
        private static readonly AccessTools.FieldRef<HexenForm, ItemDrop.ItemData> Bolt = AccessTools.FieldRefAccess<HexenForm, ItemDrop.ItemData>("_bolt");
        private static readonly AccessTools.FieldRef<HexenForm, ItemDrop.ItemData>[] Dodges =
            { AccessTools.FieldRefAccess<HexenForm, ItemDrop.ItemData>("_dodgeLeft"), AccessTools.FieldRefAccess<HexenForm, ItemDrop.ItemData>("_dodgeRight"),
              AccessTools.FieldRefAccess<HexenForm, ItemDrop.ItemData>("_dodgeUp"), AccessTools.FieldRefAccess<HexenForm, ItemDrop.ItemData>("_dodgeDown") };
        private static float dodgeStarted = float.MinValue;

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(HexenFormCombat);
            harmony.Patch(AccessTools.Method(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetDamage), new[] { typeof(int), typeof(float) }),
                postfix: new HarmonyMethod(self, nameof(GrowWithStaff)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(Humanoid), "GetCurrentBlocker"), postfix: new HarmonyMethod(self, nameof(BlockWithStaff)));
            harmony.Patch(AccessTools.Method(typeof(Humanoid), "GetAttackSpeedFactorMovement"), postfix: new HarmonyMethod(self, nameof(FlyWhileCasting)));
            harmony.Patch(AccessTools.Method(typeof(HexenForm), "Dodge"), postfix: new HarmonyMethod(self, nameof(DodgeStarted)));
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.IsDodgeInvincible)), postfix: new HarmonyMethod(self, nameof(DodgeInvulnerable)));
        }

        private static HexenForm ActiveForm(Character character) =>
            character is Player && HexenForm.TryGet(character, out var form) && form.Active ? form : null;

        // How far upgrades and damage enchantments have raised the staff's damage above its base.
        internal static float StaffGrowth(ItemDrop.ItemData staff)
        {
            if (staff == null || staff.m_shared.m_skillType != Skills.SkillType.ElementalMagic) return 1f;
            float baseDamage = staff.m_shared.m_damages.GetTotalDamage();
            return baseDamage > 0f ? Mathf.Max(1f, staff.GetDamage().GetTotalDamage() / baseDamage) : 1f;
        }

        // Attacks are computed by the player who makes them, so only the local witch's own attack items change.
        private static void GrowWithStaff(ItemDrop.ItemData __instance, ref HitData.DamageTypes __result)
        {
            var form = ActiveForm(Player.m_localPlayer);
            if (form == null) return;
            float factor = __instance == Blast(form) ? BlastFactor : __instance == Bolt(form) ? BoltFactor : 0f;
            if (factor > 0f) __result.Modify(factor * StaffGrowth(GameFields.RightItem(Player.m_localPlayer)));
        }

        private static bool IsWitchItem(HexenForm form, ItemDrop.ItemData item)
        {
            if (item == null) return false;
            if (item == Blast(form) || item == Bolt(form)) return true;
            foreach (var dodge in Dodges) if (item == dodge(form)) return true;
            return false;
        }

        private static void BlockWithStaff(Humanoid __instance, ref ItemDrop.ItemData __result)
        {
            var form = ActiveForm(__instance);
            var staff = form != null ? GameFields.RightItem(__instance) : null;
            if (staff != null && IsWitchItem(form, __result)) __result = staff;
        }

        private static void FlyWhileCasting(Humanoid __instance, ref float __result)
        {
            if (__result >= CastingFlightSpeed || !__instance.IsFlying()) return;
            var form = ActiveForm(__instance);
            var weapon = form != null ? GameFields.CurrentAttack(__instance) is Attack attack ? GameFields.AttackWeapon(attack) : null : null;
            if (weapon != null && (weapon == Blast(form) || weapon == Bolt(form))) __result = CastingFlightSpeed;
        }

        private static void DodgeStarted(HexenForm __instance)
        {
            var player = FormPlayer(__instance);
            if (player != Player.m_localPlayer || !(GameFields.CurrentAttack(player) is Attack attack)) return;
            var weapon = GameFields.AttackWeapon(attack);
            foreach (var dodge in Dodges) if (weapon == dodge(__instance)) dodgeStarted = Time.time;
        }

        // Hits are resolved by the player they land on, so the local witch's own dodge decides.
        private static void DodgeInvulnerable(Player __instance, ref bool __result)
        {
            if (__result || __instance != Player.m_localPlayer || Time.time - dodgeStarted > DodgeInvulnerableSeconds) return;
            __result = ActiveForm(__instance) != null && __instance.InAttack();
        }
    }
}
