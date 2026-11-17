using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EpicLoot;
using EpicLoot.Crafting;
using EpicLoot.CraftingV2;
using EpicLoot.ShardStones;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class AffixReplacements
    {
        private static readonly MethodInfo Available = AccessTools.Method(typeof(MagicItemEffectDefinitions),
            nameof(MagicItemEffectDefinitions.GetAvailableEffects));

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(AugmentHelper), nameof(AugmentHelper.GetAvailableAugments));
            yield return AccessTools.Method(typeof(LootRoller), nameof(LootRoller.RollAugmentEffects));
            yield return AccessTools.Method(typeof(EnchantingUIController), "GetApplyableRunesforItem");
        }

        // Augmentation uses a socket-free item; etching retains the existing rune-aware replacement rules.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            MethodBase __originalMethod)
        {
            int replacements = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(Available))
                {
                    bool augmentHelper = __originalMethod.DeclaringType == typeof(AugmentHelper);
                    bool runeEtch = __originalMethod.Name == "GetApplyableRunesforItem";
                    var selected = new CodeInstruction(augmentHelper ? OpCodes.Ldarg_0 :
                        __originalMethod.DeclaringType == typeof(LootRoller) ? OpCodes.Ldarg_2 : OpCodes.Ldarg_1);
                    selected.labels.AddRange(instruction.labels);
                    instruction.labels.Clear();
                    yield return selected;
                    if (augmentHelper)
                        yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(AugmentHelper.AugmentRecipe),
                            nameof(AugmentHelper.AugmentRecipe.EffectIndex)));
                    instruction.operand = AccessTools.Method(typeof(AffixReplacements), runeEtch ? nameof(RuneEffects) : nameof(AugmentEffects));
                    replacements++;
                }
                yield return instruction;
            }
            if (replacements != 1)
                throw new InvalidOperationException("Expected one replacement effect check in " + __originalMethod.Name);
        }

        internal static List<MagicItemEffectDefinition> AugmentEffects(ItemDrop.ItemData item, MagicItem magicItem,
            int ignoredIndex, bool lootRoll, bool augmentRoll, bool runeRoll, int selectedIndex)
        {
            var bound = item.GetMagicItem();
            var sockets = magicItem.Sockets;
            var boundSockets = bound?.Sockets;
            var empty = new List<SocketedEffect>();
            magicItem.Sockets = empty;
            if (bound != null && bound != magicItem) bound.Sockets = empty;
            // Native callers pass different flags; every augment consumer uses the same roll and augment checks.
            try { return ReplacementEffects(item, magicItem, selectedIndex, true, true, false, selectedIndex); }
            finally
            {
                magicItem.Sockets = sockets;
                if (bound != null && bound != magicItem) bound.Sockets = boundSockets;
            }
        }

        internal static List<MagicItemEffectDefinition> ReplacementEffects(ItemDrop.ItemData item, MagicItem magicItem,
            int ignoredIndex, bool lootRoll, bool augmentRoll, bool runeRoll, int selectedIndex)
        {
            string selectedType = selectedIndex >= 0 && selectedIndex < magicItem.Effects.Count
                ? magicItem.Effects[selectedIndex].EffectType : null;
            var available = MagicItemEffectDefinitions.GetAvailableEffects(item, magicItem, selectedIndex, lootRoll, augmentRoll, runeRoll);
            // The selected effect already coexists with everything retained, as a legendary's guaranteed pair does, so a new value
            // for it is never a new conflict.
            available.RemoveAll(definition => definition.Type != selectedType &&
                SocketConflicts.ReplacementConflict(definition.Type, magicItem, selectedIndex) != null);
            // The native augment dialog adds its keep-original choice separately. Do not waste a new choice on the same fixed effect.
            if (selectedType != null && MagicItemEffectDefinitions.IsValuelessEffect(selectedType))
                available.RemoveAll(definition => definition.Type == selectedType);
            return available;
        }

        private static List<MagicItemEffectDefinition> RuneEffects(ItemDrop.ItemData item, MagicItem magicItem,
            int ignoredIndex, bool lootRoll, bool augmentRoll, bool runeRoll, string selectedEffect) =>
            ReplacementEffects(item, magicItem, ignoredIndex, lootRoll, augmentRoll, runeRoll,
                magicItem.Effects.FindIndex(effect => effect.EffectType == selectedEffect));

        // Check prerequisites against the resulting item, including effects retained in other slots and socketed effects. A
        // retained effect whose prerequisite was already missing before the replacement stays dormant either way, so it cannot
        // reject an unrelated replacement.
        internal static bool PreservesRequirements(ItemDrop.ItemData item, MagicItem magicItem, MagicItemEffectDefinition candidate,
            string replacedType)
        {
            var kept = magicItem.Effects.Select(effect => effect.EffectType).ToList();
            var socketed = magicItem.GetSocketedEffects().Select(effect => effect.EffectType).ToList();
            var after = new HashSet<string>(kept.Concat(socketed)) { candidate.Type };
            var before = new HashSet<string>(kept.Concat(socketed));
            if (replacedType != null) before.Add(replacedType);
            if (!Satisfied(item, candidate.Type, after)) return false;
            // Etching retains socketed runes; augmentation calls this with no sockets.
            var retained = kept.Concat(magicItem.Sockets.Where(socket => socket?.Effect != null &&
                socket.ShardType == EpicLoot.ShardStones.ShardType.None).Select(socket => socket.Effect.EffectType)).Distinct();
            return retained.All(effectType => Satisfied(item, effectType, after) || !Satisfied(item, effectType, before));
        }

        // Socketed Bloodletting makes attacks cost health as the rolled one does, so EpicLoot's item check counts it too.
        [HarmonyPatch(typeof(MagicItemEffectRequirements), "CheckItemTypeRequirements")]
        private static class SocketedBloodletting
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var hasEffect = AccessTools.Method(typeof(ItemDataExtensions), nameof(ItemDataExtensions.HasMagicEffect));
                var code = instructions.ToList();
                int found = 0;
                for (int index = 1; index < code.Count; index++)
                {
                    if (!code[index].Calls(hasEffect) || code[index - 1].opcode != OpCodes.Ldc_I4_0) continue;
                    code[index - 1].opcode = OpCodes.Ldc_I4_1;
                    found++;
                }
                if (found != 1) throw new InvalidOperationException("Expected one rolled-only Bloodletting check in EpicLoot's item requirements.");
                return code;
            }
        }

        private static bool Satisfied(ItemDrop.ItemData item, string effectType, HashSet<string> present)
        {
            if (!MagicItemEffectDefinitions.TryGet(effectType, out var definition)) return true;
            var requirements = definition.Requirements;
            var primaryAttack = item.m_shared.m_attack;
            var secondaryAttack = item.m_shared.m_secondaryAttack;
            // Bloodletting makes attacks cost health whether it is rolled or socketed.
            bool usesHealth = primaryAttack.m_attackHealth > 0f || secondaryAttack.m_attackHealth > 0f ||
                primaryAttack.m_attackHealthPercentage > 0f || secondaryAttack.m_attackHealthPercentage > 0f ||
                present.Contains(MagicEffectType.Bloodlust);
            if (requirements.ItemUsesHealthOnAttack.HasValue && requirements.ItemUsesHealthOnAttack.Value != usesHealth) return false;
            return requirements.MustHaveEffectTypes.All(required => present.Contains(required) ||
                required == MagicEffectType.Throwable && item.m_shared.m_skillType == Skills.SkillType.Spears);
        }
    }
}
