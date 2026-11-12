using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The game refuses a consumable while its effect, or another effect of its category, is active. A lasting buff or
    // resistance can be drunk again once at most the refresh window of the active effect's lifetime is left, the lifetime
    // with EpicLoot's potion duration bonus. The new bottle replaces the old effect with a fresh one, so leftover time is
    // lost and a category keeps one effect, and EpicLoot sets its duration from the equipment worn now. Effects that
    // restore health, stamina or eitr keep their cooldowns, and food and untimed effects keep the game's rules.
    // The inventory, hotkeys, quick slots and tankards all drink through Player.CanConsumeItem and ConsumeItem;
    // MultiUserChest adds the effect of a drink from a chest another player holds open itself.
    internal static class PotionBuffRefresh
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.CanConsumeItem)),
                transpiler: new HarmonyMethod(typeof(PotionBuffRefresh), nameof(AllowRefresh)));
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.ConsumeItem)),
                transpiler: new HarmonyMethod(typeof(PotionBuffRefresh), nameof(ReplaceRefreshed)));
            if (!PluginIds.TryGet(PluginIds.MultiUserChest, out var chests)) return;
            var assembly = chests.Instance.GetType().Assembly;
            var response = AccessTools.Method(assembly.GetType("MultiUserChest.InventoryHandler", true), "RPC_RequestItemConsumeResponse",
                new[] { assembly.GetType("MultiUserChest.RequestConsumeResponse", true) });
            if (response == null) throw new InvalidOperationException("MultiUserChest's chest drink moved.");
            harmony.Patch(response, transpiler: new HarmonyMethod(typeof(PotionBuffRefresh), nameof(ReplaceRefreshed)));
        }

        // Timed, and restores no health, stamina or eitr up front or over time.
        internal static bool Lasting(StatusEffect effect) => effect.m_ttl > 0f && !(effect is SE_Stats stats &&
            (stats.m_healthUpFront > 0f || stats.m_healthOverTime > 0f || stats.m_staminaUpFront > 0f || stats.m_staminaOverTime > 0f ||
             stats.m_eitrUpFront > 0f || stats.m_eitrOverTime > 0f));

        private static bool Refreshable(StatusEffect active)
        {
            float percent = Plugin.BuffRefreshWindowPercent.Value;
            return percent > 0f && Lasting(active) && active.GetRemaningTime() <= active.m_ttl * percent / 100f;
        }

        private static IEnumerable<StatusEffect> Conflicts(SEMan effects, StatusEffect drink) => effects.GetStatusEffects().Where(active =>
            active.NameHash() == drink.NameHash() || drink.m_category.Length > 0 && active.m_category == drink.m_category);

        // Replaces the game's same-effect and category checks. The category check takes the drink's effect, not just its category.
        private static IEnumerable<CodeInstruction> AllowRefresh(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var same = AccessTools.Method(typeof(SEMan), nameof(SEMan.HaveStatusEffect), new[] { typeof(int) });
            var category = AccessTools.Method(typeof(SEMan), nameof(SEMan.HaveStatusEffectCategory));
            var categoryField = AccessTools.Field(typeof(StatusEffect), nameof(StatusEffect.m_category));
            var sameCalls = code.Where(instruction => instruction.Calls(same)).ToList();
            var categoryCalls = code.Where((instruction, index) => instruction.Calls(category) && index > 0 && code[index - 1].LoadsField(categoryField)).ToList();
            if (sameCalls.Count != 1 || categoryCalls.Count != 1) throw new InvalidOperationException("The game's consumable effect checks changed.");
            sameCalls[0].opcode = OpCodes.Call;
            sameCalls[0].operand = AccessTools.Method(typeof(PotionBuffRefresh), nameof(BlockedBySameEffect));
            var load = code[code.IndexOf(categoryCalls[0]) - 1];
            load.opcode = OpCodes.Nop;
            load.operand = null;
            categoryCalls[0].opcode = OpCodes.Call;
            categoryCalls[0].operand = AccessTools.Method(typeof(PotionBuffRefresh), nameof(BlockedByCategory));
            return code;
        }

        private static bool BlockedBySameEffect(SEMan effects, int nameHash)
        {
            var active = effects.GetStatusEffect(nameHash);
            return active != null && !Refreshable(active);
        }

        private static bool BlockedByCategory(SEMan effects, StatusEffect drink)
        {
            if (drink.m_category.Length == 0) return false;
            var conflicts = effects.GetStatusEffects().Where(active => active.m_category == drink.m_category).ToList();
            return conflicts.Count > 0 && !(Lasting(drink) && conflicts.All(Refreshable));
        }

        // The drink's effect replaces the refreshable effects it conflicts with, instead of the game resetting the old
        // effect's timer, which kept its lifetime and any other effect of the category.
        private static IEnumerable<CodeInstruction> ReplaceRefreshed(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions,
                instruction => instruction.Calls(AccessTools.Method(typeof(SEMan), nameof(SEMan.AddStatusEffect),
                    new[] { typeof(StatusEffect), typeof(bool), typeof(int), typeof(float), typeof(short) })),
                AccessTools.Method(typeof(PotionBuffRefresh), nameof(AddFresh)), 1, "A consumable's effect is added differently.");

        private static StatusEffect AddFresh(SEMan effects, StatusEffect drink, bool resetTime, int itemLevel, float skillLevel, short variant)
        {
            if (Lasting(drink))
                foreach (var active in Conflicts(effects, drink).Where(Refreshable).ToList())
                    effects.RemoveStatusEffect(active.NameHash(), true);
            return effects.AddStatusEffect(drink, resetTime, itemLevel, skillLevel, variant);
        }
    }
}
