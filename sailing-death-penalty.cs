using System;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class SailingDeathPenalty
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.Sailing, out var plugin)) return;
            var manager = plugin.Instance.GetType().Assembly.GetType("SkillManager.Skill", true);
            var nativeDeath = AccessTools.Method(typeof(Skills), nameof(Skills.OnDeath));
            // Keep Sailing in the native skill list during death so world modifiers and Fortify apply normally.
            foreach (string callback in new[] { "Patch_Skills_OnDeath_Prefix", "Patch_Skills_OnDeath_Finalizer" })
            {
                var patch = AccessTools.DeclaredMethod(manager, callback) ?? throw new MissingMethodException(manager.FullName, callback);
                harmony.Unpatch(nativeDeath, patch);
            }
        }
    }
}
