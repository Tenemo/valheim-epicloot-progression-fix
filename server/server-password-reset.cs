using System;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // FastLink restores the password from before its join attempt when the menu returns. Clear that restored value
    // afterwards, before the game reads its own -password argument, so a later server cannot inherit a saved password.
    [HarmonyPatch(typeof(FejdStartup), "Awake")]
    internal static class ServerPasswordReset
    {
        private static readonly Action<string> SetPassword = AccessTools.MethodDelegate<Action<string>>(
            AccessTools.PropertySetter(typeof(FejdStartup), nameof(FejdStartup.ServerPassword)));
        private static bool Prepare() => PluginIds.TryGet(PluginIds.FastLink, out _);
        [HarmonyAfter(PluginIds.FastLink)]
        private static void Prefix() => SetPassword(null);
    }

    // Remembering a corrected password writes the server list during the join. Its watcher must not rebuild the
    // destroyed menu or abort that connection. SetupGui reads the file again when the menu returns.
    [HarmonyPatch]
    internal static class FastLinkMenuReload
    {
        private static bool Prepare() => PluginIds.TryGet(PluginIds.FastLink, out _);
        private static MethodBase TargetMethod() => PluginIds.TryGet(PluginIds.FastLink, out var plugin)
            ? AccessTools.Method(plugin.Instance.GetType(), "ReadNewServers") : null;
        private static bool Prefix() => FejdStartup.instance != null;
    }
}
