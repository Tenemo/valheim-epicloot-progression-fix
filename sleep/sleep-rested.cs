using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Waking up grants Rested only to players who slept in a bed. When enough players sleep (LetMeSleep lowers the
    // share), Valheim's global SleepStart marks every player sleeping, and SetSleeping(false) on wake-up granted Rested
    // to awake players too, including ship passengers and helmsmen. Game.SleepStop wakes the player before detaching
    // them, so the bed flag still describes where they slept.
    [HarmonyPatch(typeof(Player), nameof(Player.SetSleeping))]
    internal static class SleepRested
    {
        private static readonly MethodInfo AddByHash = AccessTools.Method(typeof(SEMan), nameof(SEMan.AddStatusEffect),
            new[] { typeof(int), typeof(bool), typeof(int), typeof(float), typeof(short) });

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AddByHash),
                AccessTools.Method(typeof(SleepRested), nameof(GrantInBed)), 1, "Player.SetSleeping no longer grants Rested once on wake-up");

        private static StatusEffect GrantInBed(SEMan seman, int nameHash, bool resetTime, int itemLevel, float skillLevel, short variant) =>
            GameFields.StatusCharacter(seman) is Player player && player.InBed() ? seman.AddStatusEffect(nameHash, resetTime, itemLevel, skillLevel, variant) : null;
    }
}
