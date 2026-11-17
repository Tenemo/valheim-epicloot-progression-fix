using HarmonyLib;

namespace EpicLootProgressionFix
{
    // A fuling shaman's shield absorbs every hit until the next status effect update removes it once broken, so all three
    // Triple Shot arrows, which land together, stopped on a shield the first one had already broken. Let hits through a
    // shaman shield that is already broken. The hit that breaks it is still absorbed whole, and the game's update still
    // removes the shield and plays its break effect. Player protection bubbles keep the native behavior.
    [HarmonyPatch(typeof(SE_Shield), nameof(SE_Shield.OnDamaged))]
    internal static class ShamanShield
    {
        private static readonly int Shield = "GoblinShaman_shield".GetStableHashCode();

        // The game counts a shield broken once its damage exceeds its capacity.
        private static bool Prefix(SE_Shield __instance, float ___m_damage, float ___m_totalAbsorbDamage) =>
            ___m_damage <= ___m_totalAbsorbDamage || __instance.NameHash() != Shield;
    }
}
