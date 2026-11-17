using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Count each recipe material at its actual crafting cost, including zero-cost upgrade-only reagents, rather than
    // AAA_Crafting's last AzuCraftyBoxes total for any material and its raw requirement amounts.
    // Both the chest counts and the displayed Max expire after half a second: range, pulling mode and chest exclusions
    // can change without an inventory notification or a new recipe. Inventory changes still invalidate counts at once.
    internal static class CraftingMaxAmount
    {
        private const float RecountSeconds = 0.5f;
        private static readonly FieldInfo Selected = AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");
        private static readonly PropertyInfo SelectedRecipe = Selected?.FieldType.GetProperty("Recipe");
        private static readonly Dictionary<string, int> ChestCounts = new Dictionary<string, int>();
        private static AccessTools.FieldRef<bool> displayChanged;
        private static float countedAt;
        private static float refreshedAt;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            // Material supply uses AzuCraftyBoxes' chest provider.
            if (!PluginIds.TryGet(PluginIds.AaaCrafting, out var plugin) ||
                !PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out _)) return;
            var count = AccessTools.Method(plugin.Instance.GetType().Assembly.GetType("AzuAntiArthriticCrafting.Utilities.Utilities"),
                "GetAvailableItems", new[] { typeof(string) });
            var display = plugin.Instance.GetType().Assembly.GetType("AzuAntiArthriticCrafting.MaxCraftAmount");
            var update = AccessTools.Method(display, "Update");
            var changed = AccessTools.Field(display, "InventoryChanged");
            var maximum = AccessTools.Method(plugin.Instance.GetType().Assembly.GetType("AzuAntiArthriticCrafting.Patches.AAACraft"),
                "CalculateMaxCraftAmount");
            if (count == null || !count.IsStatic || count.ReturnType != typeof(int) || SelectedRecipe == null ||
                update == null || changed == null || !changed.IsStatic || changed.FieldType != typeof(bool) || maximum == null)
                throw new InvalidOperationException("AAA_Crafting's material count or Max display moved.");
            displayChanged = AccessTools.StaticFieldRefAccess<bool>(changed);
            harmony.Patch(count, prefix: new HarmonyMethod(typeof(CraftingMaxAmount), nameof(CountMaterial)));
            harmony.Patch(maximum, transpiler: new HarmonyMethod(typeof(CraftingMaxAmount), nameof(CraftingCosts)));
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(CraftingMaxAmount), nameof(RefreshDisplay)));
            harmony.Patch(AccessTools.Method(typeof(Inventory), "Changed"), postfix: new HarmonyMethod(typeof(CraftingMaxAmount), nameof(Recount)));
            log.LogInfo("AAA_Crafting's Max uses actual crafting costs and refreshes changing chest supply within half a second.");
        }

        private static IEnumerable<CodeInstruction> CraftingCosts(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var amount = AccessTools.Field(typeof(Piece.Requirement), nameof(Piece.Requirement.m_amount));
            var cost = AccessTools.Method(typeof(CraftingMaxAmount), nameof(CraftingCost));
            // AAA_Crafting checks and divides the raw amount in both its all-ingredients and any-ingredient branches.
            if (code.Count(instruction => instruction.LoadsField(amount)) != 4)
                throw new InvalidOperationException("AAA_Crafting's Max requirement calculation moved.");
            foreach (var instruction in code)
            {
                if (!instruction.LoadsField(amount)) { yield return instruction; continue; }
                instruction.opcode = OpCodes.Call;
                instruction.operand = cost;
                yield return instruction;
            }
        }

        private static int CraftingCost(Piece.Requirement requirement)
        {
            // Match Player.HaveRequirementItems and ConsumeResources: ordinary stations and upgraders use separate costs.
            var station = Player.m_localPlayer.GetCurrentCraftingStation();
            if (station != null ? station.m_upgrader != requirement.m_upgraderResource : requirement.m_upgraderResource)
                return 0;
            return requirement.GetAmount(1);
        }

        private static void RefreshDisplay()
        {
            ChestSupplyEligibility.Refresh();
            // Separate from countedAt: an amount-control query can recount without refreshing the visible label.
            if (Time.time - refreshedAt < RecountSeconds) return;
            refreshedAt = Time.time;
            Recount();
            displayChanged() = true;
        }

        // AAA_Crafting asks by name for each material of the selected recipe; its own count stays for any other item.
        private static bool CountMaterial(string itemName, ref int __result)
        {
            ChestSupplyEligibility.Refresh();
            var player = Player.m_localPlayer;
            var gui = InventoryGui.instance;
            var recipe = gui == null ? null : (Recipe)SelectedRecipe.GetValue(Selected.GetValue(gui));
            var material = recipe == null ? null : recipe.m_resources.Select(requirement => requirement?.m_resItem)
                .FirstOrDefault(item => item != null && item.m_itemData.m_shared.m_name == itemName);
            if (player == null || material == null) return true;
            if (Time.time - countedAt > RecountSeconds) Recount();
            if (!ChestCounts.TryGetValue(itemName, out int inChests))
                ChestCounts[itemName] = inChests = BuildMaterialPull.InChests(player, material);
            __result = player.GetInventory().CountItems(itemName) + inChests;
            return false;
        }

        internal static void InvalidateSupply()
        {
            Recount();
            if (displayChanged != null) displayChanged() = true;
        }

        private static void Recount()
        {
            ChestCounts.Clear();
            countedAt = Time.time;
        }
    }
}
