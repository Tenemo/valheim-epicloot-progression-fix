using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Grass and buildings set alight by cinders burn whoever stands in them, and a hurt tame is alerted and runs, so a herd
    // in the Ashlands scattered whenever a patch of grass caught. Tames now take no hit from these fires: they don't burn,
    // lose health or take fright. Cinders themselves hurt no one. Wild creatures and players still burn as before.
    [HarmonyPatch(typeof(Fire), "DoDamage")]
    internal static class TamedCinderFire
    {
        private static bool Prefix(IDestructible toHit) => !(toHit is Character creature && creature.IsTamed());
    }
}
