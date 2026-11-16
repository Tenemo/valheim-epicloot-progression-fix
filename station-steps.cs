using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The game that owns a smelter advances it every second, and a beehive every 10 seconds, and each step records the
    // time: a change sent to the server and on to every nearby player. Smelters, kilns, windmills and other smelter-type
    // stations now advance every 5 seconds, and beehives every 30. A step covers all the time since the last one, so
    // smelters make the same bars from the same fuel, only up to 5 seconds later. A beehive adds honey up to 30 seconds
    // later, and as in the game it drops the time past each honey, now up to 30 seconds instead of 10, so each honey takes
    // about 10 seconds longer on average. Adding ore or fuel and taking out bars or honey still act at once, and every game
    // still updates what the station shows. Cooking stations and ovens keep one-second steps (see CookingProgress).
    [HarmonyPatch]
    internal static class StationSteps
    {
        private const float StationSeconds = 5f;
        private const float BeehiveSeconds = 30f;

        [HarmonyPatch(typeof(Smelter), "UpdateSmelter"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> SlowSmelter(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.ReplaceOwnerCheck(instructions, typeof(Smelter), AccessTools.Method(typeof(StationSteps), nameof(StationStepDue)), true);

        // The step check also receives the update's own biome and space result, which the game computes first.
        [HarmonyPatch(typeof(Beehive), "UpdateBees"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> SlowBeehive(IEnumerable<CodeInstruction> instructions)
        {
            var due = AccessTools.Method(typeof(StationSteps), nameof(BeehiveStepDue));
            var code = Transpilers.ReplaceOwnerCheck(instructions, typeof(Beehive), due, true).ToList();
            // Valheim 1.0.17: bool flag = CheckBiome() && HaveFreeSpace(); ... if (m_nview.IsOwner() && flag)
            int space = code.FindIndex(x => x.Calls(AccessTools.Method(typeof(Beehive), "HaveFreeSpace")));
            int store = space < 0 ? -1 : code.FindIndex(space, x => x.IsStloc());
            int call = code.FindIndex(x => x.Calls(due));
            int local = store < 0 ? -1 : LocalIndex(code[store]);
            if (local < 0 || call < 0 || call + 2 >= code.Count || !code[call + 1].Branches(out _) || LocalIndex(code[call + 2]) != local || !code[call + 2].IsLdloc())
                throw new InvalidOperationException("Valheim's beehive update changed; expected its biome and space check before the owner check.");
            code.Insert(call, new CodeInstruction(OpCodes.Ldloc, local));
            return code;
        }

        private static int LocalIndex(CodeInstruction instruction)
        {
            if (instruction.opcode == OpCodes.Ldloc_0 || instruction.opcode == OpCodes.Stloc_0) return 0;
            if (instruction.opcode == OpCodes.Ldloc_1 || instruction.opcode == OpCodes.Stloc_1) return 1;
            if (instruction.opcode == OpCodes.Ldloc_2 || instruction.opcode == OpCodes.Stloc_2) return 2;
            if (instruction.opcode == OpCodes.Ldloc_3 || instruction.opcode == OpCodes.Stloc_3) return 3;
            if (instruction.operand is LocalBuilder builder) return builder.LocalIndex;
            if (instruction.operand is int index) return index;
            if (instruction.operand is byte small) return small;
            return -1;
        }

        // Smelters record each step in s_startTime, beehives in s_lastTime. The time is synced, so a
        // new owner keeps the schedule, and a new or reloaded station steps at once. Half a second of slack keeps the step
        // on every fifth or third run of the game's timer.
        // An Ashlands hot tub's owner keeps it full instead of stepping it.
        private static bool StationStepDue(ZNetView view, Smelter smelter) =>
            view.IsOwner() && AshlandsHotTubs.BurnsFuel(view, smelter) && StepDue(view.GetZDO().GetLong(ZDOVars.s_startTime, 0L), StationSeconds);

        private static bool BeehiveStepDue(ZNetView view, Beehive hive, bool biomeAndSpace) =>
            view.IsOwner() && StepDue(view.GetZDO().GetLong(ZDOVars.s_lastTime, 0L), BeehiveSeconds) && BirdNestProduction.MayProduce(view, hive, biomeAndSpace);

        private static bool StepDue(long lastStep, float seconds) =>
            lastStep == 0L || ZNet.instance.GetTime().Ticks - lastStep >= (long)((seconds - 0.5f) * TimeSpan.TicksPerSecond);
    }
}
