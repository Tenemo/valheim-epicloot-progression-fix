using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Staff of Protection reaches 5 m per quality level of the staff that cast it: 5 m at 1, 20 m at 4, 40 m at 8.
    // The cast's own copy of the application effect is sized before its first hit; the shared prefab stays unchanged.
    [HarmonyPatch(typeof(Aoe), nameof(Aoe.Setup))]
    internal static class ProtectionRadius
    {
        private const float MetresPerQuality = 5f;

        private static void Postfix(Aoe __instance, ItemDrop.ItemData item)
        {
            if (item != null && Utils.GetPrefabName(__instance.gameObject) == "staff_shield_aoe")
                __instance.m_radius = MetresPerQuality * item.m_quality;
        }
    }
}
