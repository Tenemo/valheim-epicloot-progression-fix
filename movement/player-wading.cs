using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Water a third of a character's height deep takes speed * speed / 20 off its ground speed, so the faster a player is, the
    // larger the share it takes: past 10 m/s more speed wades slower, and a 25.6 m/s sprint from Run 100 and movement speed
    // enchantments at Health Critical waded at 4.4 m/s while the same player's jog made 6.1. A player faster than their pace's
    // own speed now loses the share that pace's own speed loses, so in water as on land sprinting stays faster than jogging
    // and every speed bonus keeps its proportion. A player at or below that speed, and every creature, wades as before.
    [HarmonyPatch(typeof(Character), "ApplyLiquidResistance")]
    internal static class PlayerWading
    {
        private static void Prefix(Character __instance, ref float speed, out float __state)
        {
            __state = 1f;
            if (!(__instance is Player player)) return;
            float own = player.IsRunning() ? player.m_runSpeed : player.m_speed;
            if (own <= 0f || speed <= own) return;
            __state = speed / own;
            speed = own;
        }

        private static void Postfix(ref float speed, float __state) => speed *= __state;
    }
}
