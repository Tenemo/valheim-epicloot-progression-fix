using System.Globalization;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot's While Rested carry weight uses the comfort it recorded when Rested started or refreshed. Logout Tweaks
    // restores Rested while the character loads, before the game first measures comfort, so EpicLoot records 0 and the
    // bonus disappears until the next rest. The recorded comfort travels with Logout Tweaks' snapshot instead.
    [HarmonyPatch]
    internal static class LogoutRestedComfort
    {
        private const string ComfortKey = Plugin.Id + ".rested-comfort";
        private static readonly AccessTools.FieldRef<SE_Rested> TrackedRested =
            AccessTools.StaticFieldRefAccess<SE_Rested>(AccessTools.Field(ResourcePools.Effect("GainMaxCarryWeightFromRested"), "RestedEffect"));
        private static readonly AccessTools.FieldRef<int> TrackedComfort =
            AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(ResourcePools.Effect("GainMaxCarryWeightFromRested"), "RestedComfort"));

        private static bool Prepare() => PluginIds.TryGet(PluginIds.LogoutTweaks, out _);
        private static bool Tracked(Player player) => player == Player.m_localPlayer &&
            player.GetSEMan().GetStatusEffect(SEMan.s_statusEffectRested) is SE_Rested rested && rested == TrackedRested();

        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.Save))]
        private static void Remember(Player __instance)
        {
            if (Tracked(__instance) && TrackedComfort() > 0) __instance.m_customData[ComfortKey] = TrackedComfort().ToString(CultureInfo.InvariantCulture);
            else __instance.m_customData.Remove(ComfortKey);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Player), nameof(Player.Load))]
        [HarmonyAfter(PluginIds.LogoutTweaks), HarmonyPriority(Priority.Last)]
        private static void Restore(Player __instance)
        {
            if (!__instance.m_customData.TryGetValue(ComfortKey, out string saved)) return;
            __instance.m_customData.Remove(ComfortKey);
            if (Tracked(__instance) && int.TryParse(saved, NumberStyles.None, CultureInfo.InvariantCulture, out int comfort) && comfort > TrackedComfort())
                TrackedComfort() = comfort;
        }
    }
}
