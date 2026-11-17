using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class DiscordStartupHelp
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!DiscordBot.Installed) return;
            harmony.Patch(AccessTools.Method(DiscordBot.Discord, "Start"),
                transpiler: new HarmonyMethod(typeof(DiscordStartupHelp), nameof(RemoveAnnouncement)));
        }

        // DiscordBot sends this unsolicited command advertisement before checking its startup notification setting.
        // Remove that one send at its source; command replies and the remaining startup work retain their own paths.
        private static IEnumerable<CodeInstruction> RemoveAnnouncement(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var messages = Enumerable.Range(0, code.Count).Where(index => code[index].opcode == OpCodes.Ldstr &&
                Equals(code[index].operand, " type `!help` to find list of available commands")).ToArray();
            if (messages.Length != 1) throw new InvalidOperationException("DiscordBot's startup help announcement changed.");
            int start = messages[0] - 6, end = messages[0] + 4;
            if (start < 0 || end + 1 >= code.Count || code[start].opcode != OpCodes.Ldarg_0 ||
                !code[end].Calls(DiscordBot.SendMessage) || code.Skip(start).Take(11).Any(instruction => instruction.blocks.Count != 0) ||
                code.Skip(start + 1).Take(10).Any(instruction => instruction.labels.Count != 0))
                throw new InvalidOperationException("DiscordBot's startup help call changed.");
            code[end + 1].labels.AddRange(code[start].labels);
            code.RemoveRange(start, 11);
            return code;
        }
    }
}
