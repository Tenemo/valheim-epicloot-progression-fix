using System;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Using a Forsaken power while its effect is still on a player only restarts the effect's timer, so the activation
    // visuals, sound and message that a first use shows never play. A cooldown shortened by enchantments makes this common.
    internal static class ForsakenPowerRefresh
    {
        private static Action<StatusEffect> triggerStartEffects;

        internal static void Initialize(IntegrationPatches harmony)
        {
            triggerStartEffects = AccessTools.MethodDelegate<Action<StatusEffect>>(AccessTools.Method(typeof(StatusEffect), "TriggerStartEffects"));
            harmony.Patch(AccessTools.Method(typeof(SEMan), "Internal_AddStatusEffect"),
                prefix: new HarmonyMethod(typeof(ForsakenPowerRefresh), nameof(FindRunningPower)),
                postfix: new HarmonyMethod(typeof(ForsakenPowerRefresh), nameof(ReplayActivation)));
        }

        private static void FindRunningPower(SEMan __instance, int nameHash, bool resetTime, out StatusEffect __state)
        {
            __state = null;
            if (!resetTime) return;
            var running = __instance.GetStatusEffect(nameHash);
            if (running && running.name.StartsWith("GP_", StringComparison.Ordinal) && running.m_character) __state = running;
        }

        private static void ReplayActivation(StatusEffect __state)
        {
            if (!__state) return;
            if (!string.IsNullOrEmpty(__state.m_startMessage)) __state.m_character.Message(__state.m_startMessageType, __state.m_startMessage);
            triggerStartEffects(__state);
        }
    }
}
