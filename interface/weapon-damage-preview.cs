using System;
using System.Collections.Generic;
using System.Globalization;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using EpicLoot.src.Magic.MagicItemEffects.Helpers;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class WeaponDamagePreview
    {
        private static readonly Func<float> ConjuredFrost = AccessTools.MethodDelegate<Func<float>>(AccessTools.Method(typeof(ConjuredArrows), "GetFrostDamage"));
        private static readonly Action<Attack, HitData, float> ModifyDamage =
            AccessTools.MethodDelegate<Action<Attack, HitData, float>>(AccessTools.Method(typeof(Attack), "ModifyDamage"));

        internal static (HitData.DamageTypes Low, HitData.DamageTypes High) Range(Player player, ItemDrop.ItemData weapon,
            out ItemDrop.ItemData ammunition, out bool conjured)
        {
            var prior = WeaponTooltip.AsIfEquipped(weapon);
            try
            {
                player.GetSkills().GetRandomSkillRange(out float low, out float high, Skill(player, weapon));
                var minimum = Hit(player, weapon, low, out ammunition, out conjured);
                var maximum = Hit(player, weapon, high, out _, out _);
                return (minimum.m_damage, maximum.m_damage);
            }
            finally { HandOverride.Active = prior; }
        }

        internal static (HitData.DamageTypes Low, HitData.DamageTypes High) ToolRange(Player player, ItemDrop.ItemData weapon, Skills.SkillType skill)
        {
            var prior = WeaponTooltip.AsIfEquipped(weapon);
            try
            {
                player.GetSkills().GetRandomSkillRange(out float low, out float high, skill);
                return (Hit(player, weapon, low, out _, out _, skill).m_damage, Hit(player, weapon, high, out _, out _, skill).m_damage);
            }
            finally { HandOverride.Active = prior; }
        }

        private static HitData Hit(Player player, ItemDrop.ItemData weapon, float skillFactor, out ItemDrop.ItemData ammunition, out bool conjured, Skills.SkillType? toolSkill = null)
        {
            var hit = new HitData { m_damage = weapon.GetDamage(), m_skill = toolSkill ?? weapon.m_shared.m_skillType,
                m_skillLevel = player.GetSkillLevel(weapon.m_shared.m_skillType), m_itemLevel = (short)weapon.m_quality,
                m_itemWorldLevel = (byte)weapon.m_worldLevel, m_hitType = HitData.HitType.PlayerHit };
            hit.SetAttacker(player);
            conjured = ConjuredArrows.TryGetShotCost(weapon, out _);
            ammunition = conjured || string.IsNullOrEmpty(weapon.m_shared.m_ammoType) ? null :
                HandOverride.Active != null ? HandOverride.Active.Ammo : player.GetAmmoItem();
            if (conjured) hit.m_damage.m_frost += ConjuredFrost();
            else if (ammunition != null) hit.m_damage.Add(BowAmmoDamage.Preview(weapon, ammunition));
            var attack = weapon.m_shared.m_attack.Clone();
            GameFields.AttackCharacter(attack) = player;
            GameFields.AttackWeapon(attack) = weapon;
            // Native shots apply skill, attack, level and missing-health modifiers before status effects.
            ModifyDamage(attack, hit, skillFactor);
            // Triple Shot takes its share in the firing attack's ModifyDamage, which a cloned attack doesn't reach.
            if (Volley(player, weapon)) hit.m_damage.Modify(EffectConfig.Get(MagicEffectType.TripleBowShot, MultiShot.DAMAGE_KEY, .4f));
            player.GetSEMan().ModifyAttack(toolSkill ?? weapon.m_shared.m_skillType, ref hit);
            // Artillery adds its bonus to each fired fireball.
            if (weapon.m_shared.m_skillType == Skills.SkillType.Crossbows && player.HasActiveMagicEffect(MagicEffectType.Artillery, out float bonus))
                hit.m_damage.Modify(1f + bonus / 100f);
            return hit;
        }

        private static bool Volley(Player player, ItemDrop.ItemData weapon) =>
            (weapon.m_shared.m_skillType == Skills.SkillType.Bows || weapon.m_shared.m_skillType == Skills.SkillType.Crossbows) &&
            player.HasActiveMagicEffect(MagicEffectType.TripleBowShot);

        // How a row names the one projectile its damage covers when a shot fires several; empty for a single shot.
        internal static string PerProjectile(Player player, ItemDrop.ItemData weapon) => !Volley(player, weapon) ? "" :
            weapon.m_shared.m_skillType == Skills.SkillType.Bows ? " per arrow" : player.HasActiveMagicEffect(MagicEffectType.Artillery) ? " per fireball" : " per bolt";

        internal static Skills.SkillType Skill(Player player, ItemDrop.ItemData weapon) =>
            ElementalArchery.IsDrawnBow(weapon) && player.HasActiveMagicEffect(MagicEffectType.ElementalArchery) ? Skills.SkillType.ElementalMagic : weapon.m_shared.m_skillType;

        internal static string FormatRange(HitData.DamageTypes low, HitData.DamageTypes high)
        {
            var parts = new List<string>();
            void Add(float minimum, float maximum, string type)
            {
                if (minimum > 0 || maximum > 0) parts.Add(minimum.ToString("0.#", CultureInfo.CurrentCulture) + "-" + maximum.ToString("0.#", CultureInfo.CurrentCulture) + " " + type);
            }
            Add(low.m_damage, high.m_damage, "damage"); Add(low.m_blunt, high.m_blunt, "blunt"); Add(low.m_slash, high.m_slash, "slash"); Add(low.m_pierce, high.m_pierce, "pierce");
            Add(low.m_fire, high.m_fire, "fire"); Add(low.m_frost, high.m_frost, "frost"); Add(low.m_lightning, high.m_lightning, "lightning");
            Add(low.m_poison, high.m_poison, "poison"); Add(low.m_spirit, high.m_spirit, "spirit");
            return parts.Count == 0 ? "0" : string.Join(", ", parts);
        }
    }
}
