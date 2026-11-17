using System;
using System.Collections.Generic;
using System.Reflection;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using EpicLoot.src.Magic.MagicItemEffects.Helpers;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot gathers a magic item's tooltip values in an internal TooltipGatherer, then renders that public data as text.
    [HarmonyPatch]
    internal static class MagicTooltipContext
    {
        internal static readonly Type Gatherer = typeof(ItemTooltip).Assembly.GetType("EpicLoot.TooltipGatherer", true);
        internal static readonly Type Renderer = typeof(ItemTooltip).Assembly.GetType("EpicLoot.TooltipRenderer", true);
        internal static readonly FieldInfo Item = AccessTools.Field(Gatherer, "item");
        internal static readonly FieldInfo Data = AccessTools.Field(Gatherer, "data");
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { "GatherDamages", "GatherBlockArmor", "GatherBlockForceAndParry", "GatherKnockback" })
                yield return AccessTools.Method(Gatherer, name);
        }
        private static void Prefix(object __instance, out HandOverride.Hands __state)
        {
            __state = WeaponTooltip.AsIfEquipped((ItemDrop.ItemData)Item.GetValue(__instance));
            if (HandOverride.Active != null && !HandOverride.Active.Parry.HasValue) HandOverride.Active.Parry = false;
        }
        private static void Finalizer(HandOverride.Hands __state) => HandOverride.Active = __state;
    }

    // EpicLoot counts only the weapon's own Backstab enchantments; count every bonus that applies to it in hand.
    [HarmonyPatch]
    internal static class MagicTooltipBackstab
    {
        private static MethodBase TargetMethod() => AccessTools.Method(MagicTooltipContext.Gatherer, "GatherBackstab");
        private static void Postfix(object __instance)
        {
            var stat = ((ItemTooltipData)MagicTooltipContext.Data.GetValue(__instance)).GetStat("$item_backstab");
            if (stat == null) return;
            var item = (ItemDrop.ItemData)MagicTooltipContext.Item.GetValue(__instance);
            var prior = WeaponTooltip.AsIfEquipped(item);
            try
            {
                float bonus = MagicEffectsHelper.GetTotalActiveMagicEffectValueForWeapon(Player.m_localPlayer, item, MagicEffectType.ModifyBackstab, .01f);
                stat.Value = item.m_shared.m_backstabBonus * (1f + bonus);
                stat.Modified = bonus != 0;
            }
            finally { HandOverride.Active = prior; }
        }
    }

    [HarmonyPatch]
    internal static class NativeTooltipBlockContext
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ItemDrop.ItemData), "GetBlockPowerTooltip");
            yield return AccessTools.Method(typeof(ItemDrop.ItemData), "GetBaseBlockPower", new[] { typeof(int) });
            yield return AccessTools.Method(typeof(ItemDrop.ItemData), "GetDeflectionForce", new[] { typeof(int) });
        }
        private static void Prefix(ItemDrop.ItemData __instance, out HandOverride.Hands __state)
        {
            __state = WeaponTooltipRange.Item == __instance ? WeaponTooltip.AsIfEquipped(__instance) : HandOverride.Active;
            if (WeaponTooltipRange.Item != null && HandOverride.Active != null && !HandOverride.Active.Parry.HasValue) HandOverride.Active.Parry = false;
        }
        private static void Finalizer(HandOverride.Hands __state) => HandOverride.Active = __state;
    }
}
