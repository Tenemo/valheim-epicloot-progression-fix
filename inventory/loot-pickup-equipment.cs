using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Picking up loot never equips it. Valheim's Humanoid.Pickup equips a picked-up weapon whenever the right hand is
    // empty, so walking over a club or pickaxe put it in the player's hands. Returning recall weapons re-equip through
    // RecallPriority, and grave recovery, saved equipment and deliberate equipping use their own paths.
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.Pickup))]
    internal static class LootPickupEquipment
    {
        private static void Prefix(Humanoid __instance, ref bool autoequip)
        {
            if (__instance is Player) autoequip = false;
        }
    }
}
