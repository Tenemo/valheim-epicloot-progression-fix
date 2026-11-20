using System;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class NemesisOverheadHealthBars
    {
        private static string bossMarker;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var data = StarLevelSystem.Assembly.GetType("StarLevelSystem.common.DataObjects", true);
            bossMarker = AccessTools.Field(data, "SLS_NEMESIS_BOSS")?.GetValue(null) as string
                ?? throw new InvalidOperationException("StarLevelSystem's Nemesis boss marker changed.");
            harmony.Patch(AccessTools.Method(typeof(EnemyHud), "ShowHud"),
                postfix: new HarmonyMethod(typeof(NemesisOverheadHealthBars), nameof(ShowHud)));
        }

        private static void ShowHud(Character c)
        {
            if (c && c.IsBoss() && c.GetComponent<ZNetView>()?.GetZDO()?.GetBool(bossMarker) == true) BossOverheadHealthBars.Add(c);
        }
    }
}
