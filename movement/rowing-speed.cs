using System.Collections.Generic;
using EpicLoot;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
    internal static class RowingSpeed
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.LoadsField(AccessTools.Field(typeof(Ship), nameof(Ship.m_backwardForce))),
                AccessTools.Method(typeof(RowingSpeed), nameof(RowingForce)), 2,
                "Valheim's rowing physics changed; expected two rowing-force reads.");

        // Match EpicLoot's sail-force eligibility and multiplicative contributions. The native owner, helm, rudder
        // and steering checks stay around these reads, and the shared prefab's stored force is never changed.
        private static float RowingForce(Ship ship)
        {
            float force = ship.m_backwardForce;
            var player = Player.m_localPlayer;
            if (!player || !ship.IsPlayerInBoat(player)) return force;
            float allDay = player.GetTotalActiveMagicEffectValue(MagicEffectType.SailingSpeed, 0.01f);
            if (allDay > 0f) force *= 1f + allDay;
            if (EnvMan.IsDay())
            {
                float daytime = player.GetTotalActiveMagicEffectValue(MagicEffectType.DaySailingSpeed, 0.01f);
                if (daytime > 0f) force *= 1f + daytime;
            }
            return force;
        }
    }
}
