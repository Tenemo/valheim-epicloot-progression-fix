using System;
using System.Collections.Generic;
using System.Linq;
using EpicLoot;
using EpicLoot.Crafting;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(MagicItemEffectDefinitions), nameof(MagicItemEffectDefinitions.GetAvailableEffects))]
    internal static class RestoreEffectAfterPreview
    {
        internal static readonly Func<ItemDrop.ItemData.SharedData, ItemDrop.ItemData.SharedData> CloneShared =
            AccessTools.MethodDelegate<Func<ItemDrop.ItemData.SharedData, ItemDrop.ItemData.SharedData>>(
                AccessTools.Method(typeof(Indestructible), "Clone"));
        private static bool Prefix(ItemDrop.ItemData itemData, MagicItem magicItem, int ignoreEffectIndex,
            bool checklootroll, bool checkaugment, bool checkruneroll, ref List<MagicItemEffectDefinition> __result)
        {
            MagicItemEffect removed = null;
            var originalShared = itemData.m_shared;
            if (ignoreEffectIndex >= 0 && ignoreEffectIndex < magicItem.Effects.Count)
            {
                removed = magicItem.Effects[ignoreEffectIndex];
                magicItem.Effects.RemoveAt(ignoreEffectIndex);
            }
            try
            {
                // A socketed override changes wear, not the underlying item's replacement eligibility.
                if (!originalShared.m_useDurability && !magicItem.Effects.Any(effect => effect.EffectType == MagicEffectType.Indestructible) &&
                    Indestructible.OriginallyUsesDurability(itemData))
                {
                    itemData.m_shared = CloneShared(originalShared);
                    itemData.m_shared.m_useDurability = true;
                }
                __result = MagicItemEffectDefinitions.AllDefinitions.Values.Where(definition =>
                    definition.CheckRequirements(itemData, magicItem, checklootroll, checkaugment, checkruneroll)
                    && !EnchantCostsHelper.EffectIsDeprecated(definition)
                    && AffixReplacements.PreservesRequirements(itemData, magicItem, definition, removed?.EffectType)
                    && EnchantmentApplicability.DeadReason(itemData, magicItem, definition.Type) == null).ToList();
            }
            finally
            {
                itemData.m_shared = originalShared;
                if (removed != null) magicItem.Effects.Insert(ignoreEffectIndex, removed);
            }
            // A new value for the replaced effect is a fresh choice too; Augment's keep-original choice stays separate.
            if (removed != null && MagicItemEffectDefinitions.AllDefinitions.TryGetValue(removed.EffectType, out var original)
                && !__result.Contains(original) && !EnchantCostsHelper.EffectIsDeprecated(original)
                && EnchantmentApplicability.DeadReason(itemData, magicItem, original.Type) == null) __result.Add(original);
            return false;
        }
    }
}
