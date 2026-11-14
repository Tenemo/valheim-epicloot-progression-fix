using System.Runtime.CompilerServices;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Indestructible), nameof(Indestructible.Sync))]
    internal static class CopiedIndestructibleItems
    {
        // EpicLoot protects an item by giving it a durability-free copy of its shared data and remembering the original.
        // Every inventory addition, including each save load, protects the incoming item before storing a clone of it that
        // shares the protected copy, which EpicLoot would then remember as the clone's original. Restore the real original
        // first, so taking the protection away later brings wear back.
        private static readonly ConditionalWeakTable<ItemDrop.ItemData, ItemDrop.ItemData.SharedData> Remembered =
            (ConditionalWeakTable<ItemDrop.ItemData, ItemDrop.ItemData.SharedData>)AccessTools.Field(typeof(Indestructible), "originalShared").GetValue(null);
        private static readonly ConditionalWeakTable<ItemDrop.ItemData.SharedData, ItemDrop.ItemData.SharedData> Originals =
            new ConditionalWeakTable<ItemDrop.ItemData.SharedData, ItemDrop.ItemData.SharedData>();

        private static void Prefix(ItemDrop.ItemData item)
        {
            if (item?.m_shared != null && !Remembered.TryGetValue(item, out _) && Originals.TryGetValue(item.m_shared, out var original))
                item.m_shared = original;
        }

        private static void Postfix(ItemDrop.ItemData item)
        {
            if (item?.m_shared != null && Remembered.TryGetValue(item, out var original) && !Originals.TryGetValue(item.m_shared, out _))
                Originals.Add(item.m_shared, original);
        }

        // MultiUserChest gives each item a chest move delivers its own copy of the shared data, copied from the protected one.
        // That copy points to the same real original, or EpicLoot would remember the durability-free copy as the original.
        internal static void Initialize(IntegrationPatches patches)
        {
            if (!PluginIds.TryGet(PluginIds.MultiUserChest, out var plugin)) return;
            patches.Patch(AccessTools.Method(plugin.Instance.GetType().Assembly.GetType("MultiUserChest.InventoryHelper", true), "CloneDeeper",
                new[] { typeof(ItemDrop.ItemData) }), postfix: new HarmonyMethod(typeof(CopiedIndestructibleItems), nameof(DeeperCopy)));
        }

        private static void DeeperCopy(ItemDrop.ItemData __0, ItemDrop.ItemData __result)
        {
            if (__0?.m_shared == null || __result?.m_shared == null || ReferenceEquals(__result.m_shared, __0.m_shared) ||
                Originals.TryGetValue(__result.m_shared, out _)) return;
            if (Remembered.TryGetValue(__0, out var original) || Originals.TryGetValue(__0.m_shared, out original))
                Originals.Add(__result.m_shared, original);
        }
    }

    [HarmonyPatch(typeof(Indestructible), nameof(Indestructible.Sync))]
    internal static class BrokenIndestructibleItems
    {
        private static void Postfix(ItemDrop.ItemData item)
        {
            // Disabling durability also bypasses the game's broken-item and repair checks.
            // Keep those checks active until an actual repair restores positive durability.
            if (item != null && item.m_durability <= 0f && Indestructible.OriginallyUsesDurability(item))
                Indestructible.Revert(item);
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "RepairOneItem")]
    internal static class RepairedIndestructibleItems
    {
        private static void Postfix()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            // Repair changes durability without rewriting EpicLoot's magic data.
            foreach (var item in player.GetInventory().GetAllItems())
                if (item.HasMagicEffect(MagicEffectType.Indestructible)) Indestructible.Sync(item);
        }
    }
}
