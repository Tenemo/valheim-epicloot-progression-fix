using System;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class ChestPullingDefaults
    {
        private const string ModeKey = "ACB_PreventPulling";
        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var plugin)) return;
            var type = plugin.Instance.GetType().Assembly.GetType("AzuCraftyBoxes.Util.PlayerPullingExtensions", true);
            var mode = AccessTools.Method(type, "GetPullMode", new[] { typeof(Player) });
            if (mode == null) throw new MissingMethodException("AzuCraftyBoxes' pulling-mode reader changed.");
            harmony.Patch(mode, prefix: new HarmonyMethod(typeof(ChestPullingDefaults), nameof(ApplyMissingDefault)));
        }
        private static void ApplyMissingDefault(Player __0)
        {
            if (__0.m_customData.ContainsKey(ModeKey)) return;
            string choice = Plugin.DefaultChestPullingMode.Value;
            if (choice != "Inherit") __0.m_customData[ModeKey] = choice == "Off" ? "0" : choice == "All" ? "1" : "2";
        }
    }
}
