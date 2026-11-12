using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot's Fire Rate only shortened the pause between the bursts of an attack that fires several, and every bow,
    // crossbow and staff fires one burst except the Staff of Frost's stream, so it did nothing on them. It now also speeds
    // up the attack animation of the weapon it suits, as Attack speed does for melee weapons: a bow's release and next
    // nock, a crossbow's shot and every staff's cast, so the next draw, reload or cast starts sooner. Quick Draw's draw and
    // reload times are separate, and the Staff of Frost keeps its shorter pause between shots.
    [HarmonyPatch(typeof(ModifyAttackSpeed_ApplyAnimationHandler_Patch), nameof(ModifyAttackSpeed_ApplyAnimationHandler_Patch.ModifyAttackSpeed))]
    internal static class FireRate
    {

        internal static string EffectFor(ItemDrop.ItemData weapon)
        {
            if (weapon == null) return null;
            if (weapon.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow) return MagicEffectType.ModifyFireRate;
            var skill = weapon.m_shared.m_skillType;
            return skill == Skills.SkillType.ElementalMagic || skill == Skills.SkillType.BloodMagic ? MagicEffectType.ModifyMagicFireRate : null;
        }

        private static void Postfix(Character character, ref double __result)
        {
            if (!(character is Player player) || !player.InAttack()) return;
            var attack = GameFields.CurrentAttack(player);
            // EpicLoot now handles single-burst projectile animations itself.
            if (attack != null && attack.m_attackType == Attack.AttackType.Projectile && attack.m_projectileBursts <= 1) return;
            string effect = EffectFor(attack?.GetWeapon());
            if (effect != null) __result *= 1.0 + player.GetTotalActiveMagicEffectValue(effect, 0.01f);
        }
    }
}
