using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Each status contributes its signed change from a neutral factor. This includes custom overrides such as Puke,
    // without changing non-player regeneration. Equipment and blocking join the sum before its only zero floor.
    [HarmonyPatch]
    internal static class PlayerRegeneration
    {
        private static bool blockReward;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SEMan), nameof(SEMan.ModifyHealthRegen));
            yield return AccessTools.Method(typeof(SEMan), nameof(SEMan.ModifyStaminaRegen));
            yield return AccessTools.Method(typeof(SEMan), nameof(SEMan.ModifyEitrRegen));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var original = AccessTools.Method(typeof(StatusEffect), __originalMethod.Name);
            var replacement = AccessTools.Method(typeof(PlayerRegeneration), __originalMethod.Name);
            int changes = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(original))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                    instruction.opcode = OpCodes.Call; instruction.operand = replacement; changes++;
                }
                yield return instruction;
            }
            if (changes != 1) throw new InvalidOperationException("Valheim's status regeneration loop changed: " + __originalMethod.Name);
        }

        private static void ModifyHealthRegen(StatusEffect effect, ref float total, SEMan manager)
        {
            if (!(GameFields.StatusCharacter(manager) is Player)) { effect.ModifyHealthRegen(ref total); return; }
            float factor = 1f; effect.ModifyHealthRegen(ref factor); total += factor - 1f;
        }
        private static void ModifyStaminaRegen(StatusEffect effect, ref float total, SEMan manager)
        {
            if (!(GameFields.StatusCharacter(manager) is Player)) { effect.ModifyStaminaRegen(ref total); return; }
            float factor = 1f; effect.ModifyStaminaRegen(ref factor); total += factor - 1f;
        }
        private static void ModifyEitrRegen(StatusEffect effect, ref float total, SEMan manager)
        {
            if (!(GameFields.StatusCharacter(manager) is Player)) { effect.ModifyEitrRegen(ref total); return; }
            float factor = 1f; effect.ModifyEitrRegen(ref factor); total += factor - 1f;
        }

        // One postfix for each kind, so no call has to look up which method it follows.
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyHealthRegen))]
        private static class HealthTotal
        {
            [HarmonyPriority(Priority.Last), HarmonyAfter(PluginIds.EpicLoot)]
            private static void Postfix(SEMan __instance, ref float __0) => Adjust(__instance, ref __0, false, false);
        }

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyStaminaRegen))]
        private static class StaminaTotal
        {
            [HarmonyPriority(Priority.Last), HarmonyAfter(PluginIds.EpicLoot)]
            private static void Postfix(SEMan __instance, ref float __0) => Adjust(__instance, ref __0, true, false);
        }

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyEitrRegen))]
        private static class EitrTotal
        {
            [HarmonyPriority(Priority.Last), HarmonyAfter(PluginIds.EpicLoot)]
            private static void Postfix(SEMan __instance, ref float __0) => Adjust(__instance, ref __0, true, true);
        }

        private static void Adjust(SEMan manager, ref float total, bool blocking, bool eitr)
        {
            if (!(GameFields.StatusCharacter(manager) is Player player)) return;
            if (eitr)
            {
                total += player.GetEquipmentEitrRegenModifier();
                if (player.HasActiveMagicEffect(MagicEffectType.DeepWell))
                {
                    var config = MagicItemEffectDefinitions.GetEffectConfig(MagicEffectType.DeepWell);
                    float penalty = config != null && config.TryGetValue("EitrRegenReduction", out float configured) ? configured : 30f;
                    total -= Mathf.Clamp(penalty, 0f, 100f) * .01f;
                }
            }
            if (blocking && player.IsBlocking() && !blockReward) total -= .2f;
            total = Mathf.Max(0f, total);
        }

        internal static float Health(Player player) { float factor = 1f; player.GetSEMan().ModifyHealthRegen(ref factor); return factor; }
        internal static float Stamina(Player player) { float factor = 1f; player.GetSEMan().ModifyStaminaRegen(ref factor); return factor; }
        internal static float Eitr(Player player) { float factor = 1f; player.GetSEMan().ModifyEitrRegen(ref factor); return factor; }

        // A block or parry reward is paid while blocking, so these leave out the blocking penalty.
        internal static float BlockStamina(Player player)
        {
            blockReward = true;
            try { return Stamina(player); }
            finally { blockReward = false; }
        }
        internal static float BlockEitr(Player player)
        {
            blockReward = true;
            try { return Eitr(player); }
            finally { blockReward = false; }
        }

        // The new set penalty joins the signed sum before its single final floor.
        [HarmonyPatch]
        private static class DeepWellPenalty
        {
            private static MethodBase TargetMethod() => AccessTools.Method(
                AccessTools.Inner(typeof(DeepWell), "SEMan_ModifyEitrRegen_Patch"), "Postfix");
            private static bool Prefix() => false;
        }

        [HarmonyPatch(typeof(Player), "UpdateStats", new[] { typeof(float) })]
        private static class NativeRecovery
        {
            private static float IncludedEquipment(Player player) => 0f;
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                int blocking = 0;
                var code = Transpilers.CallInstead(instructions,
                    instruction => instruction.Calls(AccessTools.Method(typeof(Player), nameof(Player.GetEquipmentEitrRegenModifier))),
                    AccessTools.Method(typeof(NativeRecovery), nameof(IncludedEquipment)), 1, "Valheim's equipment eitr regeneration changed.");
                foreach (var instruction in code)
                    if (instruction.LoadsConstant(.8f)) { instruction.operand = 1f; blocking++; }
                if (blocking != 2) throw new InvalidOperationException("Valheim's stamina/eitr blocking penalties changed.");
                return code;
            }
        }
    }
}
