using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Players' melee weapons deal more damage and cost less stamina per attack. A melee weapon is one players can craft that
    // trains a melee skill, or the player's bare fists. Creatures' attacks, Fallen Warriors' and Shadow People's copies of
    // player weapons and boss forms have no recipe, so they keep the game's values, as do bows, staffs, pickaxes, shields and
    // torches. Chop and mining damage keep theirs too, so felling trees and breaking rocks and ore are unchanged.
    [HarmonyPatch]
    internal static class MeleeBalance
    {
        private static readonly HashSet<Skills.SkillType> MeleeSkills = new HashSet<Skills.SkillType>
        {
            Skills.SkillType.Swords, Skills.SkillType.Knives, Skills.SkillType.Clubs, Skills.SkillType.Polearms,
            Skills.SkillType.Spears, Skills.SkillType.Axes, Skills.SkillType.Unarmed,
        };

        // Each item the game creates from a prefab has its own copy of the shared data, so carried and dropped weapons are
        // recognized by their prefab, and recipe previews, which show the prefab's own item, by its shared data.
        private static HashSet<GameObject> Weapons = new HashSet<GameObject>();
        private static HashSet<ItemDrop.ItemData.SharedData> WeaponData = new HashSet<ItemDrop.ItemData.SharedData>();
        private static ItemDrop.ItemData.SharedData Fists;

        internal static bool IsMelee(ItemDrop.ItemData item) =>
            item?.m_shared != null && (item.m_shared == Fists || WeaponData.Contains(item.m_shared) || item.m_dropPrefab && Weapons.Contains(item.m_dropPrefab));

        internal static int WeaponCount => Weapons.Count;

        private static bool IsMeleeWeapon(ItemDrop.ItemData.SharedData shared) =>
            MeleeSkills.Contains(shared.m_skillType) &&
            (shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon || shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
             shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft);

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ObjectDB), "Awake");
            yield return AccessTools.Method(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB));
        }

        // Every recipe counts, enabled or not, so seasonal weapons are covered whenever their recipe opens.
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ObjectDB __instance)
        {
            var weapons = new HashSet<GameObject>();
            var data = new HashSet<ItemDrop.ItemData.SharedData>();
            foreach (var recipe in __instance.m_recipes)
            {
                var shared = recipe?.m_item?.m_itemData?.m_shared;
                if (shared == null || !IsMeleeWeapon(shared)) continue;
                weapons.Add(recipe.m_item.gameObject);
                data.Add(shared);
            }
            Weapons = weapons;
            WeaponData = data;
        }

        // Harmony patches a class either through TargetMethods or through annotated methods, so these have their own classes.
        [HarmonyPatch(typeof(Player), "Awake")]
        private static class PlayerFists
        {
            private static void Postfix(Player __instance)
            {
                if (__instance.m_unarmedWeapon) Fists = __instance.m_unarmedWeapon.m_itemData.m_shared;
            }
        }

        // Runs after EpicLoot adds its enchantment damage, so the bonus covers it and the chop an axe's added slashing brings
        // stays as it was.
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetDamage), typeof(int), typeof(float))]
        private static class Damage
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ItemDrop.ItemData __instance, ref HitData.DamageTypes __result)
            {
                float multiplier = Plugin.MeleeDamageMultiplier.Value;
                if (multiplier == 1f || !IsMelee(__instance)) return;
                __result.m_damage *= multiplier;
                __result.m_blunt *= multiplier;
                __result.m_slash *= multiplier;
                __result.m_pierce *= multiplier;
                __result.m_fire *= multiplier;
                __result.m_frost *= multiplier;
                __result.m_lightning *= multiplier;
                __result.m_poison *= multiplier;
                __result.m_spirit *= multiplier;
            }
        }

        // EpicLoot's Duelist adds block power and knockback from the weapon's damage; they keep the game's damage, since the
        // bonus is only for attacks.
        [HarmonyPatch]
        private static class DuelistBlock
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(EpicLoot.MagicItemEffects.ModifyBlock), nameof(EpicLoot.MagicItemEffects.ModifyBlock.GetDuelistBlockBonus));
                yield return AccessTools.Method(typeof(EpicLoot.MagicItemEffects.ModifyBlock), nameof(EpicLoot.MagicItemEffects.ModifyBlock.GetDuelistDeflectionBonus));
            }

            private static void Postfix(ItemDrop.ItemData item, ref float __result)
            {
                float multiplier = Plugin.MeleeDamageMultiplier.Value;
                if (__result != 0f && multiplier != 1f && IsMelee(item)) __result /= multiplier;
            }
        }

        // The game checks and pays an attack's stamina with this one cost, and the tooltip's attack cost reads it too.
        [HarmonyPatch(typeof(Attack), "GetAttackStamina")]
        private static class Stamina
        {
            private static void Postfix(Attack __instance, ref float __result)
            {
                if (__result > 0f && GameFields.AttackCharacter(__instance) is Player && IsMelee(GameFields.AttackWeapon(__instance)))
                    __result *= Plugin.MeleeAttackStaminaMultiplier.Value;
            }
        }
    }
}
