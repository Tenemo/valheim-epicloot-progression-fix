using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Player), nameof(Player.Save))]
    internal static class DeadLogoutEffects
    {
        private static bool Prepare() => PluginIds.TryGet(PluginIds.LogoutTweaks, out _);

        // Logout Tweaks writes its snapshot in a prefix. Remove only a dead character's snapshot after that write,
        // before the native save serializes custom data for respawn or logout during the death delay.
        [HarmonyAfter(PluginIds.LogoutTweaks), HarmonyPriority(Priority.Last)]
        private static void Prefix(Player __instance)
        {
            if (__instance.IsDead()) __instance.m_customData.Remove("VV_LogoutData");
        }
    }
}
