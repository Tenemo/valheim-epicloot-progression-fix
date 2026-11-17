using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Frostwalker held eitr regeneration back whenever you stood in any water, even ankle deep where it can't freeze,
    // and Eitr Barrier restarted the regeneration delay on every hit it absorbed. Frostwalker now holds it back only
    // while its ice drains your eitr, and the barrier's absorbed hits leave the delay alone.
    internal static class SetEitrRegeneration
    {
        private static readonly AccessTools.FieldRef<Player, float> RegenerationTimer = AccessTools.FieldRefAccess<Player, float>("m_eitrRegenTimer");

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(SetEitrRegeneration);
            harmony.Patch(AccessTools.Method(typeof(Frostwalker), "Tick"), transpiler: new HarmonyMethod(self, nameof(NoPauseInWater)));
            harmony.Patch(AccessTools.Method(typeof(EitrBarrier), "Absorb"), prefix: new HarmonyMethod(self, nameof(KeepTimer)), postfix: new HarmonyMethod(self, nameof(RestoreTimer)));
        }

        // Tick's only pause is the one for standing in water; the ice's drain pauses in HoldUp.
        private static IEnumerable<CodeInstruction> NoPauseInWater(IEnumerable<CodeInstruction> instructions)
        {
            var pause = AccessTools.Method(typeof(Frostwalker), "PauseEitrRegen");
            int changes = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(pause))
                {
                    changes++;
                    yield return new CodeInstruction(OpCodes.Pop).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    continue;
                }
                yield return instruction;
            }
            if (changes != 1) throw new InvalidOperationException("Frostwalker's water check changed; expected one eitr regeneration pause.");
        }

        private static void KeepTimer(Player player, out float __state) => __state = player ? RegenerationTimer(player) : 0f;

        private static void RestoreTimer(Player player, float __state)
        {
            if (player) RegenerationTimer(player) = __state;
        }
    }
}
