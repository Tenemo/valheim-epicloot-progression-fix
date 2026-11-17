using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // DiscordBot's screenshot restores the HUD whenever it is disabled or destroyed, including on the dedicated server, which has
    // no HUD and logged "Failed to show hud" at every shutdown, and on clients where no capture had hidden it. Hide and restore
    // only a live HUD, and restore it only after a capture hid it; interrupted captures still restore it.
    [HarmonyPatch]
    internal static class DiscordScreenshotHud
    {
        private static bool hidden;

        private static bool Prepare() => DiscordBot.Installed;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(DiscordBot.Screenshot, "HideHud") ?? throw new InvalidOperationException("DiscordBot's HUD hiding changed.");
            yield return AccessTools.Method(DiscordBot.Screenshot, "ShowHud") ?? throw new InvalidOperationException("DiscordBot's HUD restoring changed.");
        }

        private static bool Prefix(MethodBase __originalMethod)
        {
            if (Hud.instance == null) return false;
            if (__originalMethod.Name == "HideHud") return hidden = true;
            if (!hidden) return false;
            hidden = false;
            return true;
        }
    }
}
