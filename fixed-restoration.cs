using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Only fixed gem/enchantment awards use regeneration factors. Life steal, percentage restoration, resource
    // conversions and cost refunds keep their own amounts; this never patches the general resource methods.
    [HarmonyPatch]
    internal static class FixedRestoration
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string effect in new[] { "LifeGainOnHit", "HealthGainPerXDamageDone", "Conduit", "LifeAfterKill" })
                yield return AccessTools.Method(ResourcePools.Effect(effect), "OnDamageDealt");
            yield return AccessTools.Method(ResourcePools.Effect("GainOnBlockResource"), "GainOnBlock");
            yield return AccessTools.Method(AccessTools.Inner(ResourcePools.Effect("HealthOnEitrUse"), "UseEitr_Patch"), "Postfix");
            yield return AccessTools.Method(AccessTools.Inner(ResourcePools.Effect("Kindling"), "ApplyDamage_Patch"), "Postfix");
            yield return AccessTools.Method(AccessTools.Inner(ResourcePools.Effect("EitrWeaving"), "PatchParry_EitrWeave"), "Postfix");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var heal = AccessTools.Method(typeof(Character), nameof(Character.Heal), new[] { typeof(float), typeof(bool) });
            var stamina = AccessTools.Method(typeof(Character), nameof(Character.AddStamina));
            var eitr = AccessTools.Method(typeof(Character), nameof(Character.AddEitr));
            // Blocking is what earns these two, so its regeneration penalty doesn't shrink them.
            bool block = __originalMethod.Name == "GainOnBlock" || __originalMethod.DeclaringType.Name == "PatchParry_EitrWeave";
            int changes = 0;
            foreach (var instruction in instructions)
            {
                string replacement = instruction.Calls(heal) ? nameof(Health) : instruction.Calls(stamina) ? (block ? nameof(BlockStamina) : nameof(Stamina)) :
                    instruction.Calls(eitr) ? (block ? nameof(BlockEitr) : nameof(Eitr)) : null;
                if (replacement != null)
                {
                    instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(FixedRestoration), replacement); changes++;
                }
                yield return instruction;
            }
            int expected = __originalMethod.Name == "GainOnBlock" ? 3 : 1;
            if (changes != expected) throw new InvalidOperationException("EpicLoot's fixed restoration changed: " + __originalMethod.DeclaringType + "." + __originalMethod.Name);
        }

        private static void Health(Character target, float amount, bool showText)
        {
            if (target is Player player) amount = Mathf.Max(0f, amount) * PlayerRegeneration.Health(player);
            target.Heal(amount, showText);
        }
        private static void Stamina(Character target, float amount)
        {
            if (target is Player player) amount = Mathf.Max(0f, amount) * PlayerRegeneration.Stamina(player);
            target.AddStamina(amount);
        }
        private static void Eitr(Character target, float amount)
        {
            if (target is Player player) amount = Mathf.Max(0f, amount) * PlayerRegeneration.Eitr(player);
            target.AddEitr(amount);
        }
        private static void BlockStamina(Character target, float amount)
        {
            if (target is Player player) amount = Mathf.Max(0f, amount) * PlayerRegeneration.BlockStamina(player);
            target.AddStamina(amount);
        }
        private static void BlockEitr(Character target, float amount)
        {
            if (target is Player player) amount = Mathf.Max(0f, amount) * PlayerRegeneration.BlockEitr(player);
            target.AddEitr(amount);
        }
    }
}
