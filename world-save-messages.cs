using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The game hosting the world shows every player "World save in 30s" in the middle of the screen 30 seconds before each
    // autosave, every 10 minutes with the pack's settings, and "World saved ( 0.05+0.21s )" at the top left after
    // every world save. Both messages are dropped. Saving, the cloud storage full notice that takes the place of "World
    // saved" and the low disk space warnings, which other methods send, are unchanged.
    [HarmonyPatch]
    internal static class WorldSaveMessages
    {
        [HarmonyPatch(typeof(Game), "UpdateSaving"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> DropWarning(IEnumerable<CodeInstruction> instructions) =>
            Drop(instructions, "$msg_worldsavewarning ");

        [HarmonyPatch(typeof(ZNet), "PrintWorldSaveMessage"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> DropNotice(IEnumerable<CodeInstruction> instructions) =>
            Drop(instructions, "$msg_worldsaved ( ");

        private static void Skip(MessageHud hud, MessageHud.MessageType type, string text) { }

        // Replaces the call that sends the message starting with this text by one that takes the same arguments and does nothing.
        private static IEnumerable<CodeInstruction> Drop(IEnumerable<CodeInstruction> instructions, string text)
        {
            var code = instructions.ToList();
            var messageAll = AccessTools.Method(typeof(MessageHud), nameof(MessageHud.MessageAll));
            int start = code.FindIndex(x => x.Is(OpCodes.Ldstr, text));
            int call = start < 0 || code.FindLastIndex(x => x.Is(OpCodes.Ldstr, text)) != start ? -1 : code.FindIndex(start, x => x.Calls(messageAll));
            if (call < 0) throw new InvalidOperationException($"Valheim's world save messages changed; expected one \"{text.Trim()}\" message.");
            code[call].opcode = OpCodes.Call;
            code[call].operand = AccessTools.Method(typeof(WorldSaveMessages), nameof(Skip));
            return code;
        }
    }
}
