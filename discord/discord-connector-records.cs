using System;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // DiscordConnector 3.2.0 bundles LiteDB 5.0.21, which writes each 8 KB log page through a FileStream with an 8 KB
    // buffer. Mono keeps such a write buffered until the next one, so the last page of every commit is not yet in the
    // file. A checkpoint reads the log through another handle, finds that page missing and fails an internal check;
    // LiteDB then refuses every records call until restart. Flush the writer before any full read of the log.
    internal static class DiscordConnectorRecords
    {
        private static FieldInfo writer;
        private static object logOrigin;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.DiscordConnector, out var connector)) return;
            var assembly = connector.Instance.GetType().Assembly;
            var disk = assembly.GetType("LiteDB.Engine.DiskService");
            var origin = assembly.GetType("LiteDB.Engine.FileOrigin");
            writer = AccessTools.Field(disk, "_writer");
            var readFull = origin == null ? null : AccessTools.Method(disk, "ReadFull", new[] { origin });
            if (writer == null || writer.FieldType != typeof(Lazy<Stream>) || readFull == null)
                throw new InvalidOperationException("DiscordConnector database API changed.");
            logOrigin = Enum.Parse(origin, "Log");
            harmony.Patch(readFull, prefix: new HarmonyMethod(typeof(DiscordConnectorRecords), nameof(FlushLog)));
            log.LogInfo("DiscordConnector records log is flushed before checkpoints.");
        }

        private static void FlushLog(object __instance, object[] __args)
        {
            if (!Equals(__args[0], logOrigin)) return;
            var stream = (Lazy<Stream>)writer.GetValue(__instance);
            if (!stream.IsValueCreated) return;
            // LiteDB's log writes lock the same stream.
            lock (stream.Value) stream.Value.Flush();
        }
    }
}
