using System.Collections.Generic;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Door), nameof(Door.UseItem))]
    internal static class IntricateKeyConsumption
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData) })),
                AccessTools.Method(typeof(IntricateKeyConsumption), nameof(Consume)), 1, "Door.UseItem no longer removes one inventory entry");

        private static bool Consume(Inventory inventory, ItemDrop.ItemData key) => key.m_shared.m_name == "$item_bloodgoldkey"
            ? inventory.RemoveItem(key, 1) : inventory.RemoveItem(key);
    }
}
