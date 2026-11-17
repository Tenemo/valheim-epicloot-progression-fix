using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using EpicLoot;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Quick Stack 1.4.16 considers only items whose stack limit is above one, so tools, weapons, armor, saddles, dragon eggs
    // and other single items never reached a chest already holding the same item. An unequipped, unenchanted single item
    // now goes into a chest that holds a plain item of the same kind, quality, style and world level, each into a cell of
    // its own; a chest with only other qualities or enchanted copies keeps it out, and enchanted, unidentified or socketed
    // items stay in the bag. Dragon eggs keep matching any chest holding an egg. Favorites, protected slots, the hotbar
    // setting, equipment and quick slots, chest exclusions and pulling modes apply as before, through P, both Quick Stack
    // buttons and holding Use on a chest. Restock, sorting and the other inventory actions use their own checks and stay
    // unchanged.
    internal static class QuickStackSingleItems
    {
        private static Func<Vector2i, Vector2i, int> slotOrder;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.QuickStack, out var quickStack)) return;
            var assembly = quickStack.Instance.GetType().Assembly;
            var module = assembly.GetType("QuickStackStore.QuickStackModule", true);
            slotOrder = AccessTools.MethodDelegate<Func<Vector2i, Vector2i, int>>(AccessTools.Method(assembly.GetType("QuickStackStore.Helper", true), "CompareSlotOrder"));
            harmony.Patch(AccessTools.Method(module, "ShouldQuickStackItem"), transpiler: new HarmonyMethod(typeof(QuickStackSingleItems), nameof(IncludeSingles)));
            harmony.Patch(AccessTools.Method(module, "QuickStackIntoThisContainer"), prefix: new HarmonyMethod(typeof(QuickStackSingleItems), nameof(SetAsideUnmatched)),
                finalizer: new HarmonyMethod(typeof(QuickStackSingleItems), nameof(ReturnUnmatched)));
        }

        private static bool Egg(ItemDrop.ItemData item) => item.m_shared.m_name == "$item_dragonegg";
        private static bool Single(ItemDrop.ItemData item) => item.m_shared.m_maxStackSize <= 1 && !Egg(item);

        // Quick Stack reads item.m_shared.m_maxStackSize and lets any limit above one through.
        private static IEnumerable<CodeInstruction> IncludeSingles(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var shared = AccessTools.Field(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_shared));
            var limit = AccessTools.Field(typeof(ItemDrop.ItemData.SharedData), nameof(ItemDrop.ItemData.SharedData.m_maxStackSize));
            var reads = Enumerable.Range(1, code.Count - 1).Where(index => code[index].LoadsField(limit) && code[index - 1].LoadsField(shared)).ToList();
            if (reads.Count != 1) throw new InvalidOperationException("Quick Stack's stackable item check changed.");
            code[reads[0] - 1].opcode = OpCodes.Nop;
            code[reads[0] - 1].operand = null;
            code[reads[0]].opcode = OpCodes.Call;
            code[reads[0]].operand = AccessTools.Method(typeof(QuickStackSingleItems), nameof(StackLimit));
            return code;
        }

        private static int StackLimit(ItemDrop.ItemData item) =>
            Egg(item) || Single(item) && !item.m_equipped && !item.IsMagic() ? 2 : item.m_shared.m_maxStackSize;

        private static bool Matched(Inventory container, ItemDrop.ItemData item) => container.GetAllItems().Any(stored =>
            stored.m_shared.m_name == item.m_shared.m_name && stored.m_quality == item.m_quality && stored.m_variant == item.m_variant &&
            stored.m_worldLevel == item.m_worldLevel && !stored.IsMagic());

        // Quick Stack matches chest contents by name only; single items without a plain match in this chest wait outside its
        // turn and rejoin the list in Quick Stack's own order for the next chest.
        private static void SetAsideUnmatched(List<ItemDrop.ItemData> nonTrophies, Inventory container, out List<ItemDrop.ItemData> __state)
        {
            __state = null;
            if (nonTrophies == null) return;
            for (int index = nonTrophies.Count - 1; index >= 0; index--)
            {
                var item = nonTrophies[index];
                if (!Single(item) || Matched(container, item)) continue;
                (__state ??= new List<ItemDrop.ItemData>()).Add(item);
                nonTrophies.RemoveAt(index);
            }
        }

        private static Exception ReturnUnmatched(Exception __exception, List<ItemDrop.ItemData> nonTrophies, List<ItemDrop.ItemData> __state)
        {
            if (__state != null)
            {
                nonTrophies.AddRange(__state);
                nonTrophies.Sort((a, b) => -slotOrder(a.m_gridPos, b.m_gridPos));
            }
            return __exception;
        }
    }
}
