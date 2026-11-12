using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;

namespace EpicLootProgressionFix.Preloader
{
    public static class DiscordConnectorFileLog
    {
        private const string HarmonyId = "valheim.epiclootprogressionfix.discord-file-log";
        private static readonly object writes = new object();
        private static FieldInfo logPathField, loggerField;

        public static IEnumerable<string> TargetDLLs => Array.Empty<string>();
        public static void Patch(AssemblyDefinition assembly) { }

        public static void Initialize() => AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly.GetName().Name != "DiscordConnector") return;
            var type = args.LoadedAssembly.GetType("DiscordConnector.Common.VdcLogger", true);
            var logPath = AccessTools.Field(type, "s_logFilePath");
            var logger = AccessTools.Field(type, "s_logger");
            var log = AccessTools.DeclaredMethod(type, "LogToFileAsync", new[] { typeof(string), typeof(string) });
            if (logPath?.FieldType != typeof(string) || logger?.FieldType != typeof(ManualLogSource) || log?.ReturnType != typeof(Task))
                throw new InvalidOperationException("DiscordConnector's file logger changed.");
            var patches = Harmony.GetPatchInfo(log);
            if (patches != null)
                foreach (var prefix in patches.Prefixes)
                    if (prefix.owner == HarmonyId) return;
            logPathField = logPath;
            loggerField = logger;
            new Harmony(HarmonyId).Patch(log, prefix: new HarmonyMethod(typeof(DiscordConnectorFileLog), nameof(QueueWrite)));
            Logger.CreateLogSource("Progression Fix preloader").LogInfo("DiscordConnector file writes are serialized before startup.");
        }

        private static bool QueueWrite(string severity, string message, ref Task __result)
        {
            __result = Write((string)logPathField.GetValue(null), (ManualLogSource)loggerField.GetValue(null), severity, message);
            return false;
        }

        private static Task Write(string path, ManualLogSource log, string severity, string message)
        {
            // DiscordConnector's synchronous wrappers block on async wrappers that capture Unity's context.
            // Complete the append before returning so those wrappers never need a main-thread continuation.
            lock (writes)
            {
                try
                {
                    using (var writer = new StreamWriter(path, true))
                        writer.WriteLine($"{DateTime.Now} [{severity}]: {message}");
                }
                catch (Exception error)
                {
                    log.LogError("Error writing to log file: " + error.Message);
                }
            }
            return Task.CompletedTask;
        }
    }
}
