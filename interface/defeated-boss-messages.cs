using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The game announces a boss's arrival, the first time it notices a player and its death in the middle of every
    // player's screen, wherever they are, such as "Eikthyr summons the storm" and "Eikthyr is slain", so farming a boss
    // interrupted everyone. A boss's first defeat sets its world key, such as defeated_eikthyr. Drop these messages for
    // a boss whose key is already set when one would be sent, so only the first defeat and the fights before it are
    // announced. The game that controls the boss sends them to everyone, so the check runs there.
    [HarmonyPatch]
    internal static class DefeatedBossMessages
    {
        private static bool Defeated(Character boss) =>
            boss != null && !string.IsNullOrEmpty(boss.m_defeatSetGlobalKey) && ZoneSystem.instance != null &&
            ZoneSystem.instance.GetGlobalKey(boss.m_defeatSetGlobalKey);

        [HarmonyPatch(typeof(BaseAI), "Awake"), HarmonyPrefix]
        private static void QuietArrival(BaseAI __instance)
        {
            if (!string.IsNullOrEmpty(__instance.m_spawnMessage) && Defeated(__instance.GetComponent<Character>()))
                __instance.m_spawnMessage = "";
        }

        // Creatures hunting players call this on every AI update, so only the change to alerted is checked.
        [HarmonyPatch(typeof(BaseAI), "SetAlerted"), HarmonyPrefix]
        private static void QuietAlert(BaseAI __instance, bool alert)
        {
            if (alert && !__instance.IsAlerted() && !string.IsNullOrEmpty(__instance.m_alertedMessage) &&
                Defeated(__instance.GetComponent<Character>()))
                __instance.m_alertedMessage = "";
        }

        // Before the dying boss requests its key, so the defeat that sets it is still announced.
        [HarmonyPatch(typeof(Character), nameof(Character.OnDeath)), HarmonyPrefix]
        private static void QuietDeath(Character __instance)
        {
            var ai = __instance.GetBaseAI();
            if (ai != null && !string.IsNullOrEmpty(ai.m_deathMessage) && Defeated(__instance)) ai.m_deathMessage = "";
        }
    }
}
