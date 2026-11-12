using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // A successful crafting-skill roll adds another full craft's output instead of a flat m_craftBonusAmount items.
    [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
    internal static class CraftingBonus
    {
        private static readonly AccessTools.FieldRef<InventoryGui, Recipe> CraftRecipe =
            AccessTools.FieldRefAccess<InventoryGui, Recipe>("m_craftRecipe");
        private static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> CraftUpgradeItem =
            AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_craftUpgradeItem");

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource logger)
        {
            if (!PluginIds.TryGet(PluginIds.AaaCrafting, out var plugin)) return;
            // AAA_Crafting replays vanilla's escalating multi-craft total across its queued single crafts,
            // silently adding one more item per earlier success in the run. Each roll now doubles one craft.
            var streak = AccessTools.Method(plugin.Instance?.GetType().Assembly
                .GetType("AzuAntiArthriticCrafting.Patches.SequentialSkillBonus"), "Postfix");
            if (streak == null) throw new InvalidOperationException("AAA_Crafting's sequential skill bonus changed.");
            harmony.Patch(streak, prefix: new HarmonyMethod(typeof(CraftingBonus), nameof(SkipStreak)));
            logger.LogInfo("AAA_Crafting's sequential bonus streak is disabled; each crafting bonus adds one full craft.");
        }

        private static bool SkipStreak() => false;

        private static int BonusPerRoll(int bonusAmount, InventoryGui gui, int amount)
        {
            var recipe = CraftRecipe(gui);
            var upgrade = CraftUpgradeItem(gui);
            int bonus = bonusAmount * recipe.GetAmount(upgrade == null ? 1 : upgrade.m_quality + 1, out _, out _, 1);
            // Vanilla abandons the whole craft when its output no longer fits; forgo only the bonus instead.
            if (upgrade == null && !Player.m_localPlayer.GetInventory().CanAddItem(recipe.m_item.gameObject, amount + bonus))
                return 0;
            return bonus;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = instructions.ToList();
            var bonusAmount = AccessTools.Field(typeof(InventoryGui), nameof(InventoryGui.m_craftBonusAmount));
            int index = code.FindIndex(x => x.LoadsField(bonusAmount));
            // Valheim 1.0.15: total += m_craftBonusAmount; amount += total; so later rolls in a multi-craft escalated.
            if (index < 0 || code.FindLastIndex(x => x.LoadsField(bonusAmount)) != index || index + 6 >= code.Count ||
                code[index + 1].opcode != OpCodes.Add || !code[index + 2].IsStloc() || !code[index + 3].IsLdloc() ||
                !code[index + 4].IsLdloc() || !SameLocal(code[index + 4], code[index + 2]) ||
                code[index + 5].opcode != OpCodes.Add || !code[index + 6].IsStloc() || !SameLocal(code[index + 6], code[index + 3]))
                throw new InvalidOperationException("Valheim's crafting bonus changed; expected one bonus accumulation.");
            var perRoll = generator.DeclareLocal(typeof(int));
            // The total still drives the floating "+N" text; the output grows by exactly one craft per successful roll.
            code[index + 4].opcode = OpCodes.Ldloc;
            code[index + 4].operand = perRoll;
            code.InsertRange(index + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(code[index + 3].opcode, code[index + 3].operand),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CraftingBonus), nameof(BonusPerRoll))),
                new CodeInstruction(OpCodes.Dup),
                new CodeInstruction(OpCodes.Stloc, perRoll),
            });
            return code;
        }

        private static bool SameLocal(CodeInstruction first, CodeInstruction second) =>
            first.operand is LocalVariableInfo local && second.operand is LocalVariableInfo other && local.LocalIndex == other.LocalIndex;
    }
}
