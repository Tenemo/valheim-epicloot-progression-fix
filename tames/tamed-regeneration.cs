using System.Collections.Generic;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(BaseAI), "UpdateRegeneration")]
    internal static class TamedRegeneration
    {
        private const float RegenerationInterval = 2f, SummonFullHealSeconds = 360f;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.Heal), new[] { typeof(float), typeof(bool) })),
                AccessTools.Method(typeof(TamedRegeneration), nameof(Heal)), 1, "BaseAI.UpdateRegeneration no longer heals once");

        // Skeletons and Spirit Caller animals heal only in the postfix, so feeding changes nothing for them.
        private static void Heal(Character creature, float amount, bool showText)
        {
            if (!SummonOwnership.IsFollower(creature)) creature.Heal(amount * (creature.IsTamed() ? 10f : 1f), showText);
        }

        // Hungry tames return from the game's method before its heal, so summons heal here, right after each tick zeroes the timer.
        private static void Postfix(BaseAI __instance, float ___m_regenTimer, Character ___m_character)
        {
            if (___m_regenTimer != 0f || __instance.IsAlerted() || !SummonOwnership.IsFollower(___m_character)) return;
            ___m_character.Heal(___m_character.GetMaxHealth() / SummonFullHealSeconds * RegenerationInterval, false);
        }
    }
}
