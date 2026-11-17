using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(StoreGui))]
    internal static class TraderStacking
    {
        private static readonly FieldInfo SelectedItem = AccessTools.Field(typeof(StoreGui), "m_selectedItem");
        private static readonly Func<Inventory, bool> RefreshLimits = BindStackLimits();

        private static Func<Inventory, bool> BindStackLimits()
        {
            if (!PluginIds.TryGet(PluginIds.ItemStacks, out var plugin)) return null;
            var processing = plugin.Instance.GetType().Assembly.GetType("ItemStacksItemWeights.ItemProcessing", true);
            return AccessTools.MethodDelegate<Func<Inventory, bool>>(AccessTools.Method(processing, "PatchInventory"));
        }

        // ItemStacks refreshes carried data when opening inventory; the store opens without that callback.
        // Use its own configuration processing before offering items or paying a sale, including older coin stacks.
        [HarmonyPatch("Show"), HarmonyPrefix]
        private static void Show() => RefreshLimits?.Invoke(Player.m_localPlayer.GetInventory());

        [HarmonyPatch("SellItem"), HarmonyPrefix]
        private static void Sell() => RefreshLimits?.Invoke(Player.m_localPlayer.GetInventory());

        // A native offer contains at most one full stack. Empty ordinary or quick slots therefore suffice; a full
        // bag must fit the complete offer in stacks that the native addition and EpicLoot will actually merge.
        private static bool HasRoom(Inventory inventory, Trader.TradeItem offer)
        {
            if (inventory.HaveEmptySlot()) return true;
            if (offer?.m_prefab == null) return false;
            var item = offer.m_prefab.m_itemData;
            int remaining = Math.Min(offer.m_stack, item.m_shared.m_maxStackSize);
            foreach (var target in inventory.GetAllItems())
            {
                if (target.m_shared.m_name != item.m_shared.m_name || target.m_quality != item.m_quality ||
                    target.m_worldLevel != Game.m_worldLevel || target.m_stack >= target.m_shared.m_maxStackSize ||
                    !RuneStacks.StacksWith(target, item)) continue;
                remaining -= target.m_shared.m_maxStackSize - target.m_stack;
                if (remaining <= 0) return true;
            }
            return false;
        }

        [HarmonyPatch("UpdateBuyButton"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> BuyButton(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var emptySlot = AccessTools.Method(typeof(Inventory), nameof(Inventory.HaveEmptySlot));
            if (code.Count(instruction => instruction.Calls(emptySlot)) != 1)
                throw new InvalidOperationException("Trader purchase capacity check changed.");
            int index = code.FindIndex(instruction => instruction.Calls(emptySlot));
            code.Insert(index, new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code[index]));
            code.Insert(index + 1, new CodeInstruction(OpCodes.Ldfld, SelectedItem));
            code[index + 2].opcode = OpCodes.Call;
            code[index + 2].operand = AccessTools.Method(typeof(TraderStacking), nameof(HasRoom));
            return code;
        }

        [HarmonyPatch("BuySelectedItem"), HarmonyPrefix]
        private static bool Buy(Trader.TradeItem ___m_selectedItem) => ___m_selectedItem?.m_prefab == null ||
            HasRoom(Player.m_localPlayer.GetInventory(), ___m_selectedItem);
    }
}
