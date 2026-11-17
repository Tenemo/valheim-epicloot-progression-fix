using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class NemesisOverheadHealthBars
    {
        private static string bossMarker;
        private static FieldInfo nameField;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var data = StarLevelSystem.Assembly.GetType("StarLevelSystem.common.DataObjects", true);
            bossMarker = AccessTools.Field(data, "SLS_NEMESIS_BOSS")?.GetValue(null) as string
                ?? throw new InvalidOperationException("StarLevelSystem's Nemesis boss marker changed.");
            nameField = AccessTools.Field(AccessTools.Inner(typeof(EnemyHud), "HudData"), "m_name")
                ?? throw new MissingFieldException("Valheim's enemy HUD name moved.");
            harmony.Patch(AccessTools.Method(typeof(EnemyHud), "ShowHud"),
                postfix: new HarmonyMethod(typeof(NemesisOverheadHealthBars), nameof(ShowHud)));
        }

        private static void ShowHud(Character c, IDictionary ___m_huds)
        {
            if (!c || !c.IsBoss() || ZNet.instance != null && ZNet.instance.IsDedicated() ||
                c.GetComponent<ZNetView>()?.GetZDO()?.GetBool(bossMarker) != true) return;
            var bar = c.GetComponent<BossOverheadHealthBar>();
            if (!bar) bar = c.gameObject.AddComponent<BossOverheadHealthBar>();
            // SLS resolves and localizes the native boss HUD's name. Follow that same live text without rebuilding it.
            if (___m_huds[c] is object native) bar.NameSource = (TMPro.TMP_Text)nameField.GetValue(native);
        }
    }
}
