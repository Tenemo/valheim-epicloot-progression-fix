using HarmonyLib;

namespace EpicLootProgressionFix
{
    // An upgrade replaces the item with a new one that the game fills to full durability. EpicLoot copies the old item's
    // enchantments onto it only afterwards, so a maximum durability enchantment raised the maximum past the fill and the
    // freshly upgraded item looked worn. Fill it again once the enchantments are back.
    [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
    internal static class UpgradeDurability
    {
        internal static ItemDrop.ItemData Upgrading;

        private static void Prefix(ItemDrop.ItemData ___m_craftUpgradeItem) => Upgrading = ___m_craftUpgradeItem;

        private static void Finalizer() => Upgrading = null;
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), typeof(string), typeof(int), typeof(int), typeof(int), typeof(long), typeof(string),
        typeof(Vector2i), typeof(bool), typeof(bool), typeof(bool))]
    internal static class UpgradedItemDurability
    {
        // After EpicLoot's own postfix, which copies the enchantments onto the same item.
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ItemDrop.ItemData __result)
        {
            var upgrading = UpgradeDurability.Upgrading;
            if (upgrading == null || __result?.m_shared == null || __result.m_shared.m_name != upgrading.m_shared?.m_name) return;
            UpgradeDurability.Upgrading = null;
            __result.m_durability = __result.GetMaxDurability();
        }
    }
}
