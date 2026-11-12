using System;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Serving trays stack to the pack's limit, but each tray is still a tool with its own durability and quality. Only
    // plain trays at full durability and the same quality merge (see BossRewardStacks), so a stack never needs repair.
    // Equipping a tray from a stack first takes a single tray into a free bag cell and equips that, leaving the rest of the
    // stack unchanged; without a free cell nothing changes and the player is told why. Enchanting panels accept only a
    // single tray, as for Wishbones. The game's tray recipe is disabled, so no station upgrades or repairs trays.
    internal static class ServingTrayStacks
    {
        private static readonly Func<Inventory, bool, Vector2i> FindEmptySlot =
            AccessTools.MethodDelegate<Func<Inventory, bool, Vector2i>>(AccessTools.Method(typeof(Inventory), "FindEmptySlot"));
        private static readonly Action<Inventory, bool, bool> Changed =
            AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(AccessTools.Method(typeof(Inventory), "Changed"));

        internal static bool IsTray(ItemDrop.ItemData item) => item?.m_shared?.m_name == "$item_feaster";

        internal static bool Full(ItemDrop.ItemData item) => item.m_durability >= item.GetMaxDurability() - 0.001f;

        internal static void Initialize(IntegrationPatches harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Humanoid), nameof(Humanoid.EquipItem)),
                prefix: new HarmonyMethod(typeof(ServingTrayStacks), nameof(EquipOne)) { priority = Priority.First });
        }

        // Moves one tray of a stack into a free cell of the same inventory and returns it, or null when the bag is full.
        private static ItemDrop.ItemData TakeOne(Inventory inventory, ItemDrop.ItemData stack)
        {
            var cell = FindEmptySlot(inventory, false);
            if (cell.x < 0) return null;
            var single = stack.Clone();
            single.m_stack = 1;
            single.m_gridPos = cell;
            stack.m_stack--;
            inventory.GetAllItems().Add(single);
            Changed(inventory, false, false);
            return single;
        }

        private static bool EquipOne(Humanoid __instance, ref ItemDrop.ItemData item, ref bool __result)
        {
            if (!IsTray(item) || item.m_stack <= 1 || !(__instance is Player player) || !player.GetInventory().ContainsItem(item)) return true;
            var single = TakeOne(player.GetInventory(), item);
            if (single != null) { item = single; return true; }
            player.Message(MessageHud.MessageType.Center, "Make room in the bag to take one serving tray from the stack.");
            __result = false;
            return false;
        }
    }
}
