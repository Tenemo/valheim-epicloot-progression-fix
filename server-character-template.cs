using System;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class ServerCharacterTemplate
    {
        private static FieldInfo acquire;
        internal static bool Fresh => acquire != null && (bool)acquire.GetValue(null);
        internal static void Initialize()
        {
            if (!PluginIds.TryGet(PluginIds.ServerCharacters, out var plugin)) return;
            acquire = AccessTools.Field(plugin.Instance.GetType().Assembly.GetType("ServerCharacters.ClientSide", true), "acquireCharacterFromTemplate");
            if (acquire == null || !acquire.IsStatic || acquire.FieldType != typeof(bool))
                throw new MissingFieldException("ServerCharacters character initialization changed.");
        }
    }
}
