using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot's Earthshaker slammed for a flat 200/250/300, the same with a fresh spear as with a fully upgraded, enchanted one
    // at skill 100. Its value is now a percentage of the held weapon's damage, rolled with the weapon's skill like a swing, so it
    // keeps up with upgrades, enchantments and skill. The fall and carry-weight bonuses still apply on top.
    internal static class EarthshakerWeaponDamage
    {
        internal static void Initialize(IntegrationPatches harmony) =>
            harmony.Patch(AccessTools.Method(typeof(Earthshaker), "StartDive"),
                prefix: new HarmonyMethod(typeof(EarthshakerWeaponDamage), nameof(ScaleWithWeapon)));

        private static void ScaleWithWeapon(Player __0, ref float __1)
        {
            var weapon = __0.GetCurrentWeapon();
            if (weapon == null) { __1 = 0f; return; }
            var damage = weapon.GetDamage();
            // Tree and rock damage doesn't hurt creatures, so it doesn't count, as with Lifesteal.
            float total = damage.GetTotalDamage() - damage.m_chop - damage.m_pickaxe;
            __1 = __1 / 100f * total * __0.GetRandomSkillFactor(weapon.m_shared.m_skillType);
        }
    }
}
