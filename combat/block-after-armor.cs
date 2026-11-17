using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The game checks a block or parry against the hit before the player's resistances and body armor reduce it,
    // so a hit that armor would cut to a fraction still breaks the guard and costs stamina at full size. Apply the
    // resistances and armor first and let the block work against what is left; the game's own reduction later in
    // the same hit is skipped for that hit, so each counts once.
    internal static class BlockAfterArmor
    {
        private static readonly Func<Character, short, WeakSpot> WeakSpotOf =
            AccessTools.MethodDelegate<Func<Character, short, WeakSpot>>(AccessTools.Method(typeof(Character), "GetWeakSpot"));
        private static HitData reducedHit;
        private static HitData.DamageModifier reducedHitModifier;

        internal static void Initialize(IntegrationPatches harmony)
        {
            // First, so the other block hooks, such as StarLevelSystem's block detection, see the reduced hit.
            harmony.Patch(AccessTools.Method(typeof(Humanoid), "BlockAttack"),
                prefix: new HarmonyMethod(typeof(BlockAfterArmor), nameof(ReduceBeforeBlock)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(Character), "RPC_Damage"),
                transpiler: new HarmonyMethod(typeof(BlockAfterArmor), nameof(ReduceOnce)));
        }

        private static void ReduceBeforeBlock(Humanoid __instance, HitData hit)
        {
            if (!(__instance is Player player)) return;
            // EpicLoot's on-block conversion comes first, so the converted damage meets the player's elemental resistances.
            BlockedHits.ConvertBeforeBlock(player, hit);
            hit.ApplyResistance(player.GetDamageModifiers(WeakSpotOf(player, hit.m_weakSpot)), out reducedHitModifier);
            hit.ApplyArmor(player.GetBodyArmor());
            reducedHit = hit;
        }

        private static void ApplyResistance(HitData hit, HitData.DamageModifiers modifiers, out HitData.DamageModifier significantModifier)
        {
            if (hit == reducedHit) significantModifier = reducedHitModifier;
            else hit.ApplyResistance(modifiers, out significantModifier);
        }

        private static void ApplyArmor(HitData hit, float armor)
        {
            if (hit == reducedHit) reducedHit = null;
            else hit.ApplyArmor(armor);
        }

        private static IEnumerable<CodeInstruction> ReduceOnce(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var block = AccessTools.Method(typeof(Character), "BlockAttack");
            var resistance = AccessTools.Method(typeof(HitData), nameof(HitData.ApplyResistance),
                new[] { typeof(HitData.DamageModifiers), typeof(HitData.DamageModifier).MakeByRefType() });
            var armor = AccessTools.Method(typeof(HitData), nameof(HitData.ApplyArmor), new[] { typeof(float) });
            int blockAt = code.FindIndex(instruction => instruction.Calls(block));
            var resistanceAt = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(resistance)).ToList();
            var armorAt = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(armor)).ToList();
            // One resistance step, then armor for players and world-level armor for creatures, all after the block.
            if (blockAt < 0 || resistanceAt.Count != 1 || armorAt.Count != 2 || resistanceAt[0] < blockAt || armorAt[0] < resistanceAt[0])
                throw new InvalidOperationException("Valheim's hit order changed; expected the block before one resistance and two armor steps.");
            code[resistanceAt[0]].opcode = OpCodes.Call;
            code[resistanceAt[0]].operand = AccessTools.Method(typeof(BlockAfterArmor), nameof(ApplyResistance));
            foreach (int i in armorAt)
            {
                code[i].opcode = OpCodes.Call;
                code[i].operand = AccessTools.Method(typeof(BlockAfterArmor), nameof(ApplyArmor));
            }
            return code;
        }
    }
}
