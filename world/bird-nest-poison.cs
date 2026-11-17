using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(SpawnOnDamaged), "Start")]
    internal static class BirdNestPoison
    {
        private static bool Prefix(SpawnOnDamaged __instance) => Utils.GetPrefabName(__instance.gameObject) != "piece_birdnest";
    }
}
