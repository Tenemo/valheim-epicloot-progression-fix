using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot's Bloodletting turns a weapon's stamina cost into health, which the attack pays through the game's health
    // spending as it starts, and that flashed the screen red like a hit on every swing or throw, hit or miss. The local
    // player's payments for Bloodletting, rolled, socketed or from Hel's Court, skip only that flash. The health cost, its
    // limits, eitr substitution, sacrifice effects and lifesteal stay as they are, and every other health cost and damage
    // keeps its flash. Bloodletting needs a melee weapon that spends stamina, so projectile bursts never pay its cost.
    internal static class BloodlettingDamageFlash
    {
        private static readonly Func<Player, ItemDrop.ItemData, Attack, bool> HasBloodlust =
            AccessTools.MethodDelegate<Func<Player, ItemDrop.ItemData, Attack, bool>>(AccessTools.Method(typeof(Bloodlust), "HasBloodlust"));
        private static bool paying;

        // Takes the native Character.UseHealth arguments and the paying attack.
        private static void Pay(Character character, float health, Attack attack)
        {
            paying = character is Player player && player == Player.m_localPlayer && HasBloodlust(player, attack.GetWeapon(), attack);
            try { character.UseHealth(health); }
            finally { paying = false; }
        }

        // Takes the native Hud.DamageFlash call's instance.
        private static void DamageFlash(Hud hud)
        {
            if (!paying) hud.DamageFlash();
        }

        // Valheim 1.0.17: SetHealth(health); if (IsPlayer()) Hud.instance.DamageFlash();
        [HarmonyPatch(typeof(Character), nameof(Character.UseHealth))]
        private static class Flash
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(Hud), nameof(Hud.DamageFlash))),
                    AccessTools.Method(typeof(BloodlettingDamageFlash), nameof(DamageFlash)), 1, "Valheim's health spending changed; expected one damage flash.");
        }

        // Valheim 1.0.17: Attack.Update pays the attack's health in its first frame, passing the attack along here.
        [HarmonyPatch(typeof(Attack), nameof(Attack.Update))]
        private static class AttackPayment
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var payments = Enumerable.Range(0, code.Count).Where(index => code[index].Calls(AccessTools.Method(typeof(Character), nameof(Character.UseHealth)))).ToList();
                if (payments.Count != 1) throw new InvalidOperationException("Valheim's Attack.Update changed; expected one health payment.");
                code[payments[0]].opcode = OpCodes.Call;
                code[payments[0]].operand = AccessTools.Method(typeof(BloodlettingDamageFlash), nameof(Pay));
                code.Insert(payments[0], new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code[payments[0]]));
                return code;
            }
        }
    }
}
