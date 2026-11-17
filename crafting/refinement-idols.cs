using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Explicit corrections to native recipes; ordinary crafting and upgrade ingredients stay intact. Each idol's description then
    // names the gear its corrected recipes refine.
    [HarmonyPatch]
    internal static class RefinementIdols
    {
        private static readonly Dictionary<string, string> IdolsByItem = new Dictionary<string, string>
        {
            { "AxeFlint", "Upgrader0Weapon" },
            { "AxeBronze", "Upgrader1Weapon" },
            { "AxeIron", "Upgrader2Weapon" },
            { "BowDraugrFang", "Upgrader3Weapon" },
            { "Cultivator", "Upgrader1Weapon" },
            { "Scythe", "Upgrader3Weapon" },
            { "Shovel", "Upgrader6Weapon" },
            { "AxeJotunBane", "Upgrader5Weapon" },
            { "HelmetBerserkerHood", "Upgrader1Armor" },
            { "SpearSplitner", "Upgrader6Weapon" },
            { "SpearSplitner_Blood", "Upgrader6Weapon" },
            { "SpearSplitner_Lightning", "Upgrader6Weapon" },
            { "SpearSplitner_Nature", "Upgrader6Weapon" },
            { "AtgeirGold", "Upgrader7Weapon" },
            { "AxeGold", "Upgrader7Weapon" },
            { "AxeGold_FrostFire", "Upgrader7Weapon" },
            { "FistGold", "Upgrader7Weapon" },
            { "SledgeStagbreaker", "Upgrader1Weapon" },
            { "KnifeChitin", "Upgrader2Weapon" },
            { "ShieldSerpentscale", "Upgrader2Armor" },
            { "KnifeSkollAndHati", "Upgrader5Weapon" },
            { "Demister", "Upgrader5Armor" },
            { "HelmetDNHeavy", "Upgrader7Armor" },
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ObjectDB), "Awake");
            yield return AccessTools.Method(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB));
        }

        private static void Postfix(ObjectDB __instance)
        {
            foreach (var recipe in __instance.m_recipes)
            {
                if (!recipe.m_enabled || recipe.m_item == null ||
                    !IdolsByItem.TryGetValue(recipe.m_item.gameObject.name, out string idolName)) continue;
                var idol = __instance.GetItemPrefab(idolName)?.GetComponent<ItemDrop>();
                if (idol == null) throw new InvalidOperationException("Refinement requires missing native idol " + idolName + ".");
                var requirement = recipe.m_resources.FirstOrDefault(resource => resource.m_upgraderResource);
                if (requirement == null)
                {
                    requirement = new Piece.Requirement
                    {
                        m_amount = 1, m_amountPerLevel = 0, m_upgraderResource = true, m_recover = false,
                    };
                    recipe.m_resources = recipe.m_resources.Concat(new[] { requirement }).ToArray();
                }
                requirement.m_resItem = idol;
            }
            Describe(__instance);
        }

        // Every enabled recipe the Forge of Potential lists, under the idol it takes as native refinement selects it, by the
        // game's own item name tokens. Each description is built whole, so a copied or reloaded database, or a festival recipe
        // switched on later, repeats nothing.
        internal static void Describe(ObjectDB database)
        {
            var gear = new Dictionary<ItemDrop, Dictionary<string, ItemDrop>>();
            foreach (var recipe in database.m_recipes)
            {
                if (!recipe.m_enabled || recipe.m_item == null || recipe.m_item.m_itemData.m_shared.m_maxQuality <= 1) continue;
                var idol = recipe.m_resources.FirstOrDefault(resource => resource.m_upgraderResource)?.m_resItem;
                if (idol == null) continue;
                if (!gear.TryGetValue(idol, out var items)) gear[idol] = items = new Dictionary<string, ItemDrop>();
                items[recipe.m_item.gameObject.name] = recipe.m_item;
            }
            foreach (var entry in gear)
            {
                // Armor first, then shields and capes, each by its name in the game's language.
                var names = entry.Value.Values.Select(item => item.m_itemData.m_shared)
                    .OrderBy(item => item.m_itemType == ItemDrop.ItemData.ItemType.Shield ? 1 : item.m_itemType == ItemDrop.ItemData.ItemType.Shoulder ? 2 : 0)
                    .ThenBy(item => Localization.instance.Localize(item.m_name), StringComparer.InvariantCulture)
                    .Select(item => item.m_name).ToList();
                string list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names.Last();
                var shared = entry.Key.m_itemData.m_shared;
                // Native refinement succeeds below the upgrade chance and destroys the item from one minus the break chance.
                int success = Mathf.RoundToInt(shared.m_upgradeChance * 100f);
                int destroyed = Mathf.RoundToInt((1f - Mathf.Max(shared.m_upgradeChance, 1f - shared.m_breakChance)) * 100f);
                shared.m_description = "This mysterious idol could grant a powerful boon, or demand a great sacrifice.\n\n" +
                    "Works on: " + list + ".\n\nUse at: $piece_upgradestation.\n\n" +
                    $"Consumes 1 idol per attempt. {success}% chance: quality +1. {destroyed}% chance: equipment destroyed.";
            }
        }
    }
}
