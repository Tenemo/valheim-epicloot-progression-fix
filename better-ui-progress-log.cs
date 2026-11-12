using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // BetterUI 2.5.13 wrote "Updated level progress percent to ..." to every player's game log each time a skill gained
    // experience, over 2,000 lines an hour. That line is dropped; the level progress it reports is unchanged.
    internal static class BetterUiProgressLog
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.BetterUi, out var plugin)) return;
            var progress = AccessTools.Method(plugin.Instance.GetType().Assembly.GetType("BetterUI.Patches.XP"), "UpdateLevelProgressPercentage");
            if (progress == null) throw new InvalidOperationException("BetterUI's level progress changed.");
            harmony.Patch(progress, transpiler: new HarmonyMethod(typeof(BetterUiProgressLog), nameof(Transpiler)));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = instructions.ToList();
            var logger = AccessTools.Field(original.DeclaringType.Assembly.GetType("BetterUI.Main"), "log");
            var logDebug = AccessTools.Method(typeof(ManualLogSource), nameof(ManualLogSource.LogDebug));
            int start = code.FindIndex(x => x.LoadsField(logger));
            int end = code.FindIndex(x => x.Calls(logDebug));
            // BetterUI 2.5.13: LevelProgressPercentage = ...; Main.log.LogDebug(string.Format("...", LevelProgressPercentage));
            if (logger == null || start < 0 || end <= start || code.FindLastIndex(x => x.LoadsField(logger)) != start ||
                code.FindLastIndex(x => x.Calls(logDebug)) != end || code.Skip(start).Take(end - start + 1).Any(x => x.labels.Count > 0))
                throw new InvalidOperationException("BetterUI's level progress changed; expected one debug line.");
            code.RemoveRange(start, end - start + 1);
            return code;
        }
    }
}
