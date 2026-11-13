using System;
using EpicLoot;
using EpicLoot.Adventure;
using EpicLoot.Adventure.Feature;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Andvaranaut's treasure and bounty finder is an enchantment bought once from Haldor. The ring he sells carries the only
    // copy, which the enchanting table can extract into a rune for a shardstone slot; no roll ever gives it. EpicLoot keeps
    // the enchantment in the ring's prefab and gives the finder to every ring, so a ring stripped of its enchantment would
    // get it back the next time it loaded and would keep finding. The prefab ring is plain here instead, Haldor's sale
    // writes the copy onto the ring it sells, and the finder follows the enchantment wherever it is worn or slotted. Rings
    // already sold carry their copy in their own saved data and keep it.
    internal static class AndvaranautEnchantment
    {
        internal const string Feature = "Andvaranaut enchantment";
        internal const string Prefab = "Andvaranaut";
        private const float RefreshInterval = 0.25f;

        private static MagicItem template;
        private static bool selling;
        private static float refreshTimer;

        // The finder status effect EpicLoot made for the ring, now given by the enchantment.
        internal static SE_CustomFinder Finder { get; private set; }

        // EpicLoot's own setup of the ring, once per game start, before any world loads.
        [HarmonyPatch(typeof(global::EpicLoot.EpicLoot), "SetupAndvaranaut")]
        private static class PlainRing
        {
            private static void Postfix()
            {
                var ring = EpicAssets.AssetBundle.LoadAsset<GameObject>(Prefab)?.GetComponent<ItemDrop>()?.m_itemData;
                var magic = ring?.GetMagicItem();
                var finder = ring?.m_shared.m_equipStatusEffect as SE_CustomFinder;
                if (magic == null || !magic.HasEffect(MagicEffectType.Andvaranaut) || finder == null)
                {
                    DisabledFeatures.Set(Feature, "EpicLoot no longer sets up the Andvaranaut ring with its enchantment and finder");
                    return;
                }
                template = magic;
                Finder = finder;
                ring.RemoveMagicItem();
                ring.m_shared.m_equipStatusEffect = null;
            }
        }

        // A fresh copy for one ring, so extracting it from one item never changes another.
        internal static void Give(ItemDrop.ItemData item)
        {
            if (template == null || item == null || item.IsMagic()) return;
            var copy = new MagicItem { Rarity = template.Rarity, TypeNameOverride = template.TypeNameOverride };
            foreach (var effect in template.Effects) copy.Effects.Add(new MagicItemEffect(effect.EffectType, effect.EffectValue));
            item.SaveMagicItem(copy);
        }

        // Haldor's stock shows the ring he sells with its enchantment.
        [HarmonyPatch(typeof(SecretStashItemInfo), MethodType.Constructor, typeof(string), typeof(ItemDrop.ItemData), typeof(Currencies), typeof(bool))]
        private static class StockRow
        {
            private static void Postfix(string itemId, ItemDrop.ItemData item, bool isGamble)
            {
                if (!isGamble && itemId == Prefab) Give(item);
            }
        }

        // A purchase creates the ring from its plain prefab; only that one ring gets the copy, never a gamble.
        [HarmonyPatch(typeof(MerchantPanel), nameof(MerchantPanel.BuyItem))]
        private static class Sale
        {
            private static void Prefix(BuyListElement listItem) =>
                selling = listItem?.ItemInfo != null && !listItem.ItemInfo.IsGamble && listItem.ItemInfo.ItemID == Prefab;
            private static Exception Finalizer(Exception __exception) { selling = false; return __exception; }
        }

        [HarmonyPatch(typeof(AdventureFeature), nameof(AdventureFeature.CreateItemDrop))]
        private static class SoldRing
        {
            private static void Postfix(ItemDrop __result)
            {
                if (!selling || __result == null) return;
                selling = false;
                Give(__result.m_itemData);
            }
        }

        // The local player has the finder while an equipped item carries the enchantment, on the item or in a shardstone slot.
        [HarmonyPatch(typeof(Player), "Update")]
        private static class Wearer
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                refreshTimer -= Time.deltaTime;
                if (refreshTimer > 0f) return;
                refreshTimer = RefreshInterval;
                Refresh(__instance);
            }
        }

        internal static void Refresh(Player player)
        {
            if (Finder == null)
            {
                DisabledFeatures.Set(Feature, "the Andvaranaut ring was not set up before the world loaded");
                return;
            }
            var effects = player.GetSEMan();
            int hash = Finder.NameHash();
            bool wanted = player.HasActiveMagicEffect(MagicEffectType.Andvaranaut);
            if (wanted && !effects.HaveStatusEffect(hash)) effects.AddStatusEffect(Finder);
            else if (!wanted && effects.HaveStatusEffect(hash)) effects.RemoveStatusEffect(hash);
        }
    }
}
