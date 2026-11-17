using System;
using System.Collections.Generic;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The game delivers normal chat only to nearby players, but sends shouts to the whole server,
    // and DiscordBot relays only shouts to Discord. Normal chat is therefore sent as a shout. Shouts keep their
    // yellow color and the speaker's name on floating text, but show what was typed instead of capitals.
    internal static class ServerWideChat
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Chat), nameof(Chat.SendText)),
                prefix: new HarmonyMethod(typeof(ServerWideChat), nameof(SendToEveryone)));
            var chatLine = AccessTools.Method(typeof(Terminal), "AddString",
                new[] { typeof(PlatformUserID), typeof(string), typeof(Talker.Type), typeof(bool) });
            var floatingText = AccessTools.Method(typeof(Chat), "AddInworldText",
                new[] { typeof(GameObject), typeof(long), typeof(Vector3), typeof(Talker.Type), typeof(UserInfo), typeof(string) });
            if (chatLine == null || floatingText == null) throw new InvalidOperationException("Valheim's chat display changed.");
            foreach (var display in new[] { chatLine, floatingText })
                harmony.Patch(display, transpiler: new HarmonyMethod(typeof(ServerWideChat), nameof(KeepShoutCase)));
        }

        // Whispers stay local. DiscordBot's postfix on SendText sees the changed type and relays the message.
        private static void SendToEveryone(ref Talker.Type type)
        {
            if (type == Talker.Type.Normal) type = Talker.Type.Shout;
        }

        private static string AsTyped(string text) => text;

        private static IEnumerable<CodeInstruction> KeepShoutCase(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(AccessTools.Method(typeof(string), nameof(string.ToUpper), Type.EmptyTypes)),
                AccessTools.Method(typeof(ServerWideChat), nameof(AsTyped)), 1, "Valheim's shout display changed; expected one capitalization.");
    }
}
