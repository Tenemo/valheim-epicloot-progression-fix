using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class DyrrBindingStorage
    {
        private static string bindingPath;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource logger)
        {
            if (!PluginIds.TryGet(PluginIds.Dyrr, out var plugin)) return;
            var getter = AccessTools.PropertyGetter(plugin.Instance.GetType().Assembly.GetType("Dyrr.Home", true), "HomePath");
            if (getter == null || !getter.IsStatic || getter.ReturnType != typeof(string))
                throw new MissingMemberException("Dyrr binding storage API changed.");

            // Gale scans every config file, and text files elsewhere in the profile too.
            // A .dat file outside config stays private and survives both imports and pulls.
            string directory = Path.Combine(Paths.BepInExRootPath, "dyrr-state");
            bool hadDirectory = Directory.Exists(directory);
            IntegrationChanges.Defer(() => Directory.CreateDirectory(directory), () =>
            {
                if (!hadDirectory && Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory);
            });
            bindingPath = Path.Combine(directory, "homes.dat");
            harmony.Patch(getter, prefix: new HarmonyMethod(typeof(DyrrBindingStorage), nameof(HomePath)));

            // Preserve complete local binding rows, never replace durable state with a later shared import.
            foreach (string name in new[] { "dyrr-home.txt", "threshold-home.txt", "boon-home.txt" })
            {
                string legacy = Path.Combine(Paths.ConfigPath, name);
                if (!File.Exists(legacy)) continue;
                string retained = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + "-" + DateTime.UtcNow.Ticks + ".dat");
                bool copied = false, moved = false;
                IntegrationChanges.Defer(() =>
                {
                    Directory.CreateDirectory(directory);
                    if (!File.Exists(bindingPath))
                        using (var source = File.OpenRead(legacy))
                        using (var target = new FileStream(bindingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            copied = true;
                            source.CopyTo(target);
                        }
                    File.Move(legacy, retained); moved = true;
                }, () =>
                {
                    if (moved) File.Move(retained, legacy);
                    if (copied) File.Delete(bindingPath);
                });
            }
            logger.LogInfo("Dyrr bindings persist in BepInEx/dyrr-state/homes.dat outside Gale-managed config.");
        }

        private static bool HomePath(ref string __result)
        {
            __result = bindingPath;
            return false;
        }
    }
}
