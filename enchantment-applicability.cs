using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpicLoot;
using EpicLoot.CraftingV2;
using EpicLoot.LegendarySystem;
using EpicLoot.MagicItemEffects;
using EpicLoot_UnityLib;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // An effect is completely dead on an item when no supported use of the item gives it any result: its usable attacks,
    // ammunition, Northern Vengeance's retaliation hits, its set and the other worn gear all count, so a weak or
    // situational effect stays. Fresh effects are never dead: the shared effect pool behind loot, identification,
    // enchanting, Augment and rune etching leaves them out, etching and sockets refuse them, and a named item is not chosen
    // for a base its guarantees would do nothing on. Existing affixes are repaired once by EnchantmentMigration; existing
    // sockets keep their occupants. Each rule names a dead pairing and one that stays.
    [HarmonyPatch]
    internal static class EnchantmentApplicability
    {
        private static readonly Func<ItemDrop.ItemData, ItemDrop.ItemData, int, MagicItem> EtchResult = AccessTools.MethodDelegate<Func<ItemDrop.ItemData, ItemDrop.ItemData, int, MagicItem>>(
            AccessTools.Method(typeof(EnchantingUIController), "BuildRuneEtchResult"));

        // Effects that only scale the held item's own damage; retaliation, summons and Echo Spike's returning orb do not use it.
        private static readonly HashSet<string> WeaponDamage = new HashSet<string>
        {
            MagicEffectType.ModifyDamage, MagicEffectType.ModifyDamageLowHealth, MagicEffectType.ModifyPhysicalDamage, MagicEffectType.ModifyElementalDamage,
            MagicEffectType.AddBluntDamage, MagicEffectType.AddSlashingDamage, MagicEffectType.AddPiercingDamage, MagicEffectType.AddFireDamage,
            MagicEffectType.AddFrostDamage, MagicEffectType.AddLightningDamage, MagicEffectType.AddPoisonDamage, MagicEffectType.AddSpiritDamage,
            MagicEffectType.IncreaseDamageDuringDaytime, MagicEffectType.IncreaseDamageDuringNighttime, MagicEffectType.DamageBonusFromPlayerWeight,
            MagicEffectType.DamageIncreaseFromMovementPenalty,
        };
        // Effects that act only on a block or parry, which a held build tool never makes. The fishing rod reels by blocking.
        private static readonly HashSet<string> Blocking = new HashSet<string>
        {
            MagicEffectType.ModifyBlockStaminaUse, MagicEffectType.ModifyBlockPower, MagicEffectType.ModifyBlockPowerLowHealth, MagicEffectType.ModifyBlockForce,
            MagicEffectType.ModifyParry, MagicEffectType.ModifyParryLowHealth, MagicEffectType.ModifyParryWindow, MagicEffectType.PhysToFireOnBlock,
            MagicEffectType.PhysToFrostOnBlock, MagicEffectType.PhysToPoisonOnBlock, MagicEffectType.PhysToLightningOnBlock, MagicEffectType.LifeGainOnBlock,
            MagicEffectType.StaminaGainOnBlock, MagicEffectType.EitrGainOnBlock, MagicEffectType.Warding, MagicEffectType.ElementalWarding,
            MagicEffectType.LuckyBlock, MagicEffectType.StaggerOnBlock, MagicEffectType.KineticQuake, MagicEffectType.BurdenedBlock,
            MagicEffectType.AnchoredBlock, MagicEffectType.BloodBaseBlock, MagicEffectType.BloodStaggerBlock, MagicEffectType.DayBlocker,
            MagicEffectType.NightBlocker, MagicEffectType.BlockAsWoodCuttingAndPickaxes,
        };
        // Effects of the held item's own hits. Retaliation keeps them working on staves that deal no damage themselves.
        private static readonly HashSet<string> Hits = new HashSet<string>
        {
            MagicEffectType.EitrLeech, MagicEffectType.ChanceDoubleDamage, MagicEffectType.LifeGainOnHit, MagicEffectType.HealthGainPerXDamageDone,
            MagicEffectType.EitrImbueAttack,
        };
        private static readonly HashSet<Skills.SkillType> ChainSkills = new HashSet<Skills.SkillType>
        {
            Skills.SkillType.Swords, Skills.SkillType.Clubs, Skills.SkillType.Knives, Skills.SkillType.Unarmed, Skills.SkillType.Axes, Skills.SkillType.Polearms,
            Skills.SkillType.Spears, Skills.SkillType.Bows, Skills.SkillType.Crossbows, Skills.SkillType.ElementalMagic, Skills.SkillType.BloodMagic,
        };
        private static readonly HashSet<Skills.SkillType> MeleeSkills = new HashSet<Skills.SkillType>
        {
            Skills.SkillType.Swords, Skills.SkillType.Knives, Skills.SkillType.Clubs, Skills.SkillType.Polearms, Skills.SkillType.Spears, Skills.SkillType.Axes,
            Skills.SkillType.Unarmed,
        };
        // Every effect a rule below covers; the effect pool asks about every effect.
        private static readonly HashSet<string> Covered = new HashSet<string>(WeaponDamage.Concat(Blocking).Concat(Hits).Concat(new[]
        {
            MagicEffectType.ModifyProjectileSpeed, MagicEffectType.OffSetAttack, MagicEffectType.ChainLightning, MagicEffectType.ModifyAttackEitrUse,
            MagicEffectType.Indestructible, MagicEffectType.RecallWeapon, MagicEffectType.Apportation, MagicEffectType.CloseQuarter,
            MagicEffectType.ModifyDrawStaminaUse, MagicEffectType.ModifyAttackStaminaUse, MagicEffectType.IncreaseHarvestDamage,
            MagicEffectType.IncreaseMeleeSkills, MagicEffectType.ModifyAttackHealthUse, MagicEffectType.FrostDamageAOE,
            MagicEffectType.ModifySummonDamage, MagicEffectType.ModifySummonHealth,
        }));
        private static bool checkingOccupants;

        // Why the effect would do nothing on the item, or null when it has a use there.
        internal static string DeadReason(ItemDrop.ItemData item, MagicItem magic, string effectType)
        {
            if (!Covered.Contains(effectType)) return null;
            var shared = item.m_shared;
            var type = shared.m_itemType;
            // A weapon in the hands attacks alone; a torch's effects leave the other hand's weapon alone too. Equipping a
            // build tool empties both hands, and its attack places pieces.
            bool weapon = type == ItemDrop.ItemData.ItemType.OneHandedWeapon || type == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                type == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft || type == ItemDrop.ItemData.ItemType.Bow;
            bool torch = type == ItemDrop.ItemData.ItemType.Torch;
            bool builds = shared.m_buildPieces != null;
            bool held = weapon || torch || builds;
            bool ammunition = !string.IsNullOrEmpty(shared.m_ammoType);
            // Staves, bows and crossbows keep a secondary attack without an animation, which no control can start.
            var attacks = new[] { shared.m_attack, shared.m_secondaryAttack }.Where(attack => attack != null && !string.IsNullOrEmpty(attack.m_attackAnimation)).ToList();
            bool damage = Total(shared.m_damages) > 0f || Total(shared.m_damagesPerLevel) > 0f || ammunition;

            // Dead Raiser and the other casts that summon or protect; Echo Spike and bows launch real projectiles.
            if (effectType == MagicEffectType.ModifyProjectileSpeed)
                return weapon && !ammunition && !attacks.Any(attack => attack.m_attackProjectile && attack.m_attackProjectile.GetComponent<Projectile>()) &&
                    !Throwable(item, magic) ? "it launches no projectile" : null;
            // The Scythe, Snow Shovel, Sparkler, Hammer and Blood Magic staves; an iron sword, any bow and any shield stay.
            if (WeaponDamage.Contains(effectType))
                return builds ? "a build tool never attacks" : (weapon || torch) && !damage ? "it deals no weapon damage" : null;
            // Spears, fist weapons and the fire staff stop at two strikes; an iron sword and armor stay.
            if (effectType == MagicEffectType.OffSetAttack)
                return builds ? "a build tool never attacks" : weapon && attacks.All(attack => attack.m_attackChainLevels < 3) ? "no attack reaches a third combo strike" : null;
            // Pickaxes, the Scythe and the fishing rod; swords stay.
            if (effectType == MagicEffectType.ChainLightning)
                return !ChainSkills.Contains(shared.m_skillType) ? "its hits use a skill chain lightning ignores" : null;
            // Dundr pays its eitr while reloading; the fire staff, spellsword weapons and drawn bows stay.
            if (effectType == MagicEffectType.ModifyAttackEitrUse)
                return held && !attacks.Any(attack => attack.m_attackEitr > 0f || attack.m_bowDraw) && !SpellbladeMagicSwords.IsSpellswordOnceMagic(item) ? "its attacks cost no eitr" : null;
            // The Dverger circlet and seasonal hats; an iron helmet stays.
            if (effectType == MagicEffectType.Indestructible)
                return !Indestructible.OriginallyUsesDurability(item) ? "it never wears out" : null;
            // The Abyssal Harpoon's line returns no weapon; a flint spear and anything that can be made throwable stay.
            if (effectType == MagicEffectType.RecallWeapon || effectType == MagicEffectType.Apportation)
                return !attacks.Any(attack => attack.m_attackProjectile && attack.m_attackProjectile.GetComponent<Projectile>()?.m_respawnItemOnHit == true) &&
                    !Throwable(item, magic) ? "it never throws itself" : null;
            // The Scythe and Snow Shovel; bows push with their ammunition.
            if (effectType == MagicEffectType.CloseQuarter)
                return shared.m_attackForce <= 0f && !ammunition ? "its hits push nothing back" : null;
            if (Blocking.Contains(effectType))
                return builds ? "a build tool cannot block" : null;
            if (Hits.Contains(effectType))
                return builds ? "a build tool never hits" : null;
            // Crossbows reload instead of drawing; bows and the fishing rod stay. Only the item's own value applies.
            if (effectType == MagicEffectType.ModifyDrawStaminaUse)
                return shared.m_attack.m_drawStaminaDrain <= 0f ? "it has no bow draw" : null;
            // Bows, crossbows and the fishing rod spend no attack stamina, and building reads its cost directly.
            if (effectType == MagicEffectType.ModifyAttackStaminaUse)
                return builds ? "a build tool never attacks" : held && !attacks.Any(attack => attack.m_attackStamina > 0f) ? "its attacks cost no stamina" : null;
            // An iron sword has no chop or pickaxe damage; the Arbalest chops.
            if (effectType == MagicEffectType.IncreaseHarvestDamage)
                return (held || type == ItemDrop.ItemData.ItemType.Tool) && shared.m_damages.m_chop + shared.m_damagesPerLevel.m_chop <= 0f &&
                    shared.m_damages.m_pickaxe + shared.m_damagesPerLevel.m_pickaxe <= 0f ? "it deals no chop or pickaxe damage" : null;
            // Pickaxes, farming tools, the fishing rod and the Grappling Hook; spears and a Sparkler in the other hand stay.
            if (effectType == MagicEffectType.IncreaseMeleeSkills)
                return builds ? "a build tool never attacks" : weapon && !MeleeSkills.Contains(shared.m_skillType) ? "it uses no melee skill" : null;
            // Bows, crossbows and elemental staves never spend health; Dead Raiser and anything Bloodletting fits stay.
            if (effectType == MagicEffectType.ModifyAttackHealthUse)
                return held && !attacks.Any(attack => attack.m_attackHealth > 0f || attack.m_attackHealthPercentage > 0f) &&
                    !(attacks.Any(attack => attack.m_attackStamina > 0f) && (magic.HasEffect(MagicEffectType.Bloodlust, includeSocketed: true) || Allows(item, magic, MagicEffectType.Bloodlust)))
                    ? "its attacks cannot cost health" : null;
            // Bows, sledges and the Scythe; an iron sword and armor stay.
            if (effectType == MagicEffectType.FrostDamageAOE)
                return builds ? "a build tool never attacks" : weapon && (!damage || !attacks.Any(attack => attack.m_attackType == Attack.AttackType.Horizontal ||
                    attack.m_attackType == Attack.AttackType.Vertical)) ? "it has no melee swing" : null;
            // The bonuses reach tamed magic summons as they appear. Trollstav's troll is not tamed and the other casts summon
            // nothing; Dead Raiser, Spirit Caller and Hel's staff, whose full set raises thralls, stay.
            if (effectType == MagicEffectType.ModifySummonDamage || effectType == MagicEffectType.ModifySummonHealth)
                return weapon && !attacks.Any(Summons) && !RaisesThralls(magic) ? "it summons nothing these bonuses reach" : null;
            return null;
        }

        internal static string Refusal(ItemDrop.ItemData item, string reason) =>
            "No effect on " + Localization.instance.Localize(item.m_shared.m_name) + ": " + reason + ".";

        // The occupants an item already has stay whatever they do; only a new one must do something.
        internal static bool CheckingOccupants => checkingOccupants;

        internal static bool KeepingOccupants(Func<bool> check)
        {
            checkingOccupants = true;
            try { return check(); }
            finally { checkingOccupants = false; }
        }

        private static float Total(HitData.DamageTypes damage) => damage.m_damage + damage.m_blunt + damage.m_slash + damage.m_pierce + damage.m_chop +
            damage.m_pickaxe + damage.m_fire + damage.m_frost + damage.m_lightning + damage.m_poison + damage.m_spirit;

        private static bool Allows(ItemDrop.ItemData item, MagicItem magic, string effectType) =>
            MagicItemEffectDefinitions.TryGet(effectType, out var definition) &&
            definition.Requirements.CheckRequirements(item, new MagicItem { Rarity = magic.Rarity }, effectType, checklootroll: false);

        // A throw made from the item itself: a rolled or socketed Throwable, or one it could still receive.
        private static bool Throwable(ItemDrop.ItemData item, MagicItem magic) =>
            magic.HasEffect(MagicEffectType.Throwable, includeSocketed: true) || Allows(item, magic, MagicEffectType.Throwable);

        private static bool Summons(Attack attack) =>
            attack.m_attackProjectile && attack.m_attackProjectile.GetComponent<SpawnAbility>()?.m_spawnPrefab?.Any(prefab => prefab &&
                prefab.GetComponent<Tameable>() is Tameable tame && tame.m_startsTamed &&
                (tame.m_levelUpOwnerSkill == Skills.SkillType.BloodMagic || tame.m_levelUpOwnerSkill == Skills.SkillType.ElementalMagic)) == true;

        private static bool RaisesThralls(MagicItem magic) =>
            UniqueLegendaryHelper.TryGetLegendarySetInfo(magic.SetID, out var set) && set.SetBonuses.Any(bonus => bonus?.Effect?.Type == MagicEffectType.HelsHarvest);

        // Why a named item's guarantees, together on this base with its set, would include an effect with no use, or null.
        internal static string DeadGuarantee(ItemDrop.ItemData item, LegendaryInfo template, ItemRarity rarity)
        {
            var guaranteed = template.GuaranteedMagicEffects ?? new List<GuaranteedMagicEffect>();
            var probe = new MagicItem { Rarity = rarity, LegendaryID = template.ID, SetID = UniqueLegendaryHelper.GetSetForLegendaryItem(template) };
            probe.Effects.AddRange(guaranteed.Select(effect => new MagicItemEffect(effect.Type, 0f)));
            return guaranteed.Select(effect => DeadReason(item, probe, effect.Type) is string reason ? effect.Type + ": " + reason : null).FirstOrDefault(reason => reason != null);
        }

        [HarmonyPatch]
        private static class NamedTemplates
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(UniqueLegendaryHelper), nameof(UniqueLegendaryHelper.GetAvailableUniques));
                yield return AccessTools.Method(typeof(UniqueLegendaryHelper), nameof(UniqueLegendaryHelper.GetAvailableSetPieces));
            }

            private static void Postfix(ItemDrop.ItemData baseItem, ItemRarity rarity, List<LegendaryInfo> __result) =>
                __result.RemoveAll(template => DeadGuarantee(baseItem, template, rarity) != null);
        }

        // Etching gives the item each effect of the rune: the first in the chosen slot, the others in place of a weaker one of
        // the same type or as new affixes. Each one it gives must do something there; the rune list shows only such runes.
        [HarmonyPostfix, HarmonyPatch(typeof(EnchantingUIController), "BuildRuneEtchResult")]
        private static void Etch(ItemDrop.ItemData item, ref MagicItem __result)
        {
            var before = item?.GetMagicItem();
            if (__result == null || before == null) return;
            for (int slot = 0; slot < __result.Effects.Count; slot++)
            {
                var effect = __result.Effects[slot];
                bool given = slot >= before.Effects.Count || before.Effects[slot].EffectType != effect.EffectType || before.Effects[slot].EffectValue != effect.EffectValue;
                if (given && DeadReason(item, __result, effect.EffectType) != null) { __result = null; return; }
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(EnchantingUIController), "GetApplyableRunesforItem")]
        private static void EtchableRunes(ItemDrop.ItemData item, string selectedEffect, List<InventoryItemListElement> __result)
        {
            int slot = item.GetMagicItem()?.Effects.FindIndex(effect => effect.EffectType == selectedEffect) ?? -1;
            __result.RemoveAll(rune => EtchResult(item, rune.GetItem(), slot) == null);
        }
    }
}
