using System;
using System.Collections.Generic;
using System.Linq;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // For a weapon you are not holding, EpicLoot's tooltip counts only that weapon's own enchantments: no shard
    // bonuses and nothing from your other gear, so it disagrees with the same weapon once equipped. Work the numbers
    // out as if it were equipped instead, replacing whatever the game would take out of your hands for it.
    // Arrows and bolts got your gear's bonuses only while selected, together with those of the weapon in your hands.
    // Work them out on their own instead, wherever they lie and whether or not they are selected: their own damage and
    // enchantments with your worn gear, set bonuses and buffs, but without the weapon in your hands or other selected
    // ammunition. A shot still adds the weapon's damage and bonuses as before.
    [HarmonyPatch(typeof(ModifyDamage), nameof(ModifyDamage.GetDamageWithMagicEffects))]
    internal static class WeaponTooltip
    {
        private static readonly Func<Humanoid, ItemDrop.ItemData, ItemDrop.ItemData> FindAmmo =
            AccessTools.MethodDelegate<Func<Humanoid, ItemDrop.ItemData, ItemDrop.ItemData>>(AccessTools.Method(typeof(Attack), "FindAmmo"));
        private static void Prefix(ItemDrop.ItemData item, out HandOverride.Hands __state) => __state = AsIfEquipped(item);

        private static void Finalizer(HandOverride.Hands __state) => HandOverride.Active = __state;

        // Puts the item in the local player's hands for EpicLoot, unless it is already equipped, or selects
        // ammunition with nothing in the hands. Returns the hands to set back once the numbers are worked out.
        internal static HandOverride.Hands AsIfEquipped(ItemDrop.ItemData item)
        {
            var previous = HandOverride.Active;
            var player = Player.m_localPlayer;
            if (item == null || player == null) return previous;
            bool tooltip = WeaponTooltipRange.Item != null;
            if (tooltip && item.IsUnidentified()) return previous;
            if (tooltip && previous?.Player == player && (previous.Right == item || previous.Left == item || previous.Ammo == item)) return previous;
            // Selected ammunition already counts as equipped.
            if (IsAmmo(item))
                HandOverride.Active = new HandOverride.Hands { Player = player, Ammo = item, Equipped = player.GetAmmoItem() == item ? null : item };
            else if ((!player.IsItemEquiped(item) || tooltip) && HandsAfterEquipping(player, item, out var right, out var left))
            {
                var hands = new HandOverride.Hands { Player = player, Right = right, Left = left, Ammo = player.GetAmmoItem(), Equipped = item };
                HandOverride.Active = hands;
                try
                {
                    if (tooltip && !string.IsNullOrEmpty(item.m_shared.m_ammoType) && !ConjuredArrows.TryGetShotCost(item, out _))
                    {
                        hands.Ammo = FindAmmo(player, item);
                        hands.PreviewValues.Clear();
                    }
                }
                catch { HandOverride.Active = previous; throw; }
            }
            return previous;
        }

        // Ammunition a player can select and fire; turret ammunition never gets a player's bonuses.
        internal static bool IsAmmo(ItemDrop.ItemData item) => Is(item, ItemDrop.ItemData.ItemType.Ammo);

        // The hands after equipping an item, following Humanoid.EquipItem; false for items not held in the hands.
        private static bool HandsAfterEquipping(Humanoid humanoid, ItemDrop.ItemData item, out ItemDrop.ItemData right, out ItemDrop.ItemData left)
        {
            right = GameFields.RightItem(humanoid);
            left = GameFields.LeftItem(humanoid);
            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                    right = item;
                    left = null;
                    return true;
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                    right = null;
                    left = item;
                    return true;
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                    if (Is(right, ItemDrop.ItemData.ItemType.Torch) && left == null) left = right;
                    if (!Is(left, ItemDrop.ItemData.ItemType.Shield) && !Is(left, ItemDrop.ItemData.ItemType.Torch)) left = null;
                    right = item;
                    return true;
                case ItemDrop.ItemData.ItemType.Shield:
                    if (!Is(right, ItemDrop.ItemData.ItemType.OneHandedWeapon) && !Is(right, ItemDrop.ItemData.ItemType.Torch)) right = null;
                    left = item;
                    return true;
                case ItemDrop.ItemData.ItemType.Torch:
                    if (Is(right, ItemDrop.ItemData.ItemType.OneHandedWeapon) && left == null) left = item;
                    else
                    {
                        if (!Is(left, ItemDrop.ItemData.ItemType.Shield)) left = null;
                        right = item;
                    }
                    return true;
                default:
                    return false;
            }
        }

        private static bool Is(ItemDrop.ItemData item, ItemDrop.ItemData.ItemType type) => item != null && item.m_shared.m_itemType == type;
    }

    // A tooltip's damage range comes from the skill bonuses of the weapons in hand, so work it out as if the weapon
    // were equipped as well. Only the range: the tooltip's totals for your current equipment, such as movement speed,
    // keep describing what you wear. EpicLoot builds a magic item's whole tooltip in its own prefix, so note the item
    // before that runs. Ammunition's range uses the skill of the weapons that fire it, and the game's own ammunition
    // tooltip reads the damage straight from the item rather than through EpicLoot's helper, so work that out the
    // same way too.
    [HarmonyPatch]
    internal static class WeaponTooltipRange
    {
        internal static ItemDrop.ItemData Item;
        private static readonly Dictionary<string, Skills.SkillType> FiringSkills = new Dictionary<string, Skills.SkillType>();

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void BeginTooltip(ItemDrop.ItemData item, out ItemDrop.ItemData __state)
        {
            __state = Item;
            Item = item;
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
        [HarmonyFinalizer]
        private static void EndTooltip(ItemDrop.ItemData __state) => Item = __state;

        // Before EpicLoot's prefix, which works the range out and skips the game's.
        [HarmonyPatch(typeof(Skills), nameof(Skills.GetRandomSkillRange)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void BeginRange(ref Skills.SkillType skillType, out HandOverride.Hands __state)
        {
            __state = WeaponTooltip.AsIfEquipped(Item);
            if (WeaponTooltip.IsAmmo(Item) && skillType == Item.m_shared.m_skillType) skillType = FiringSkill(Item);
            else if (Item != null && skillType == Item.m_shared.m_skillType && ElementalArchery.IsDrawnBow(Item) &&
                Player.m_localPlayer && Player.m_localPlayer.HasActiveMagicEffect(MagicEffectType.ElementalArchery)) skillType = Skills.SkillType.ElementalMagic;
        }

        [HarmonyPatch(typeof(Skills), nameof(Skills.GetRandomSkillRange)), HarmonyFinalizer]
        private static void EndRange(HandOverride.Hands __state) => HandOverride.Active = __state;

        // EpicLoot adds its bonuses to the damage in its postfix, which runs inside this.
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetDamage), typeof(int), typeof(float)), HarmonyPrefix]
        private static void BeginAmmoDamage(ItemDrop.ItemData __instance, out HandOverride.Hands __state) =>
            __state = __instance == Item ? WeaponTooltip.AsIfEquipped(__instance) : HandOverride.Active;

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetDamage), typeof(int), typeof(float)), HarmonyFinalizer]
        private static void EndAmmoDamage(HandOverride.Hands __state) => HandOverride.Active = __state;

        // The skill of the weapons that use this ammunition, such as Crossbows for bolts; its own skill if none does.
        private static Skills.SkillType FiringSkill(ItemDrop.ItemData ammo)
        {
            string type = ammo.m_shared.m_ammoType;
            if (string.IsNullOrEmpty(type)) return ammo.m_shared.m_skillType;
            if (FiringSkills.TryGetValue(type, out var skill)) return skill;
            var weapon = ObjectDB.instance.m_items.Select(prefab => prefab.GetComponent<ItemDrop>())
                .FirstOrDefault(drop => drop != null && drop.m_itemData.IsWeapon() && drop.m_itemData.m_shared.m_ammoType == type);
            return FiringSkills[type] = weapon != null ? weapon.m_itemData.m_shared.m_skillType : ammo.m_shared.m_skillType;
        }
    }
}
