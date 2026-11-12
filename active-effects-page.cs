using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EpicLoot;
using EpicLoot.Compendium;
using EpicLoot.MagicItemEffects;
using EpicLoot.MagicItemEffects.Shards;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    // The raven book had two effect pages: the game's list of status effects, each with its own description, and
    // EpicLoot's list of the enchantments rolled on worn items, which left out runes, shardstones and set bonuses. One
    // Active effects page at the top of the book replaces both. It shows what applies right now, worked out with the
    // calculations the game itself uses: health, stamina and eitr with their regeneration, armor and the share of each
    // damage type that gets through every resistance, block and parry, movement and stamina costs, the current weapon's
    // hits and boosted skills. Every other enchantment follows in its section with its total across equipment,
    // runes, shardstones and set bonuses, so nothing is listed twice and nothing per item. It updates while open. Hovering a
    // status, food or Forsaken power row shows what it does, in the window item tooltips use.
    [HarmonyPatch]
    internal static class ActiveEffectsPage
    {
        private const float RefreshSeconds = 0.5f;
        private const string ValueColor = "#ffa500", MutedColor = "#9a9a9a", HeadingColor = "#ffa626";
        // Smaller than EpicLoot's 40, 24 and 18 point title, headings and lines.
        private const int TitleFontSize = 30, HeadingFontSize = 20, RowFontSize = 16;
        private const float TitleHeight = 50f, HeadingHeight = 34f, RowSpacing = 2f, ColumnSpacing = 24f;
        private static readonly CultureInfo Numbers = CultureInfo.InvariantCulture;

        private static readonly AccessTools.FieldRef<TextsDialog, List<TextsDialog.TextInfo>> Texts =
            AccessTools.FieldRefAccess<TextsDialog, List<TextsDialog.TextInfo>>("m_texts");
        private static readonly AccessTools.FieldRef<Skills, Dictionary<Skills.SkillType, Skills.Skill>> SkillData =
            AccessTools.FieldRefAccess<Skills, Dictionary<Skills.SkillType, Skills.Skill>>("m_skillData");
        private static readonly AccessTools.FieldRef<Humanoid, float> BlockTimer = AccessTools.FieldRefAccess<Humanoid, float>("m_blockTimer");
        private static readonly AccessTools.FieldRef<SE_Demister, GameObject> WispBall = AccessTools.FieldRefAccess<SE_Demister, GameObject>("m_ballInstance");
        private static readonly Func<Player, float> DodgeStamina = AccessTools.MethodDelegate<Func<Player, float>>(AccessTools.Method(typeof(Player), "GetDodgeStaminaUse"));
        private static readonly Func<Character, float> JogFactor = AccessTools.MethodDelegate<Func<Character, float>>(AccessTools.Method(typeof(Character), "GetJogSpeedFactor"));
        private static readonly Func<Character, float> RunFactor = AccessTools.MethodDelegate<Func<Character, float>>(AccessTools.Method(typeof(Character), "GetRunSpeedFactor"));
        private static readonly Action<MagicTextElement, string> SetText = AccessTools.MethodDelegate<Action<MagicTextElement, string>>(AccessTools.Method(typeof(MagicTextElement), "Set"));
        private static readonly AccessTools.FieldRef<MagicTextList, VerticalLayoutGroup> ListLayout = AccessTools.FieldRefAccess<MagicTextList, VerticalLayoutGroup>("_layout");
        private static readonly AccessTools.FieldRef<MagicTextList, ScrollRect> ListScroll = AccessTools.FieldRefAccess<MagicTextList, ScrollRect>("_scrollRect");
        private static readonly AccessTools.FieldRef<MagicTextElement, GameObject> ElementObject = AccessTools.FieldRefAccess<MagicTextElement, GameObject>("_obj");
        private static readonly AccessTools.FieldRef<UITooltip> ShownTooltip = AccessTools.StaticFieldRefAccess<UITooltip>(AccessTools.Field(typeof(UITooltip), "m_current"));

        private static readonly (HitData.DamageType Type, string Name)[] DamageTypes =
        {
            (HitData.DamageType.Blunt, "Blunt"), (HitData.DamageType.Slash, "Slash"), (HitData.DamageType.Pierce, "Pierce"),
            (HitData.DamageType.Fire, "Fire"), (HitData.DamageType.Frost, "Frost"), (HitData.DamageType.Lightning, "Lightning"),
            (HitData.DamageType.Poison, "Poison"), (HitData.DamageType.Spirit, "Spirit"),
        };

        private enum Section { Resources, Defense, Movement, Attacks, Skills, Utility, Other }

        private static readonly string[] SectionNames = { "Health and resources", "Defense", "Movement", "Attacks", "Skills", "Utility", "Other effects" };

        // Enchantments the computed rows already include, so they are not listed again.
        private static readonly HashSet<string> Included = new HashSet<string>
        {
            MagicEffectType.AddAllSkills, MagicEffectType.Interconnected, MagicEffectType.ConvertHealthToEitr, MagicEffectType.DeepWell,
            MagicEffectType.SlightlyWeakToFire, MagicEffectType.WeakToFire, MagicEffectType.VeryWeakToFire,
            MagicEffectType.IncreaseHealth, MagicEffectType.IncreaseStamina, MagicEffectType.IncreaseEitr, MagicEffectType.PercentHealth,
            MagicEffectType.PercentStamina, MagicEffectType.PercentEitr, MagicEffectType.BulkUp, MagicEffectType.EnergeticEitr,
            MagicEffectType.HeartyEitr, MagicEffectType.EveryXPointsOfEitrIncreasesStamina, MagicEffectType.GainMaxStaminaBasedOnPlayerMaxHealth,
            MagicEffectType.StaminaIncreaseForMovementPenalty, MagicEffectType.DartingThoughts, MagicEffectType.ModifyHealthRegen,
            MagicEffectType.AddHealthRegen, MagicEffectType.ModifyStaminaRegen, MagicEffectType.ModifyEitrRegen,
            MagicEffectType.StaminaRegenBonusFromPlayerWeight, MagicEffectType.BloodMagicLevelIncreasesHealthRegen, MagicEffectType.ModifyLowHealth,
            MagicEffectType.ModifyArmor, MagicEffectType.ArmorFromMovementPenalty, MagicEffectType.ReduceArmorIncreaseDamage,
            MagicEffectType.AddFireResistancePercentage, MagicEffectType.AddFrostResistancePercentage, MagicEffectType.AddLightningResistancePercentage,
            MagicEffectType.AddPoisonResistancePercentage, MagicEffectType.AddSpiritResistancePercentage, MagicEffectType.AddElementalResistancePercentage,
            MagicEffectType.AddBluntResistancePercentage, MagicEffectType.AddSlashingResistancePercentage, MagicEffectType.AddPiercingResistancePercentage,
            MagicEffectType.AddChoppingResistancePercentage, MagicEffectType.AddPhysicalResistancePercentage,
            MagicEffectType.PhysToFire, MagicEffectType.PhysToFrost, MagicEffectType.PhysToPoison, MagicEffectType.PhysToLightning,
            MagicEffectType.ModifyBlockPower, MagicEffectType.ModifyParry, MagicEffectType.ModifyBlockForce, MagicEffectType.ModifyParryWindow,
            MagicEffectType.BurdenedBlock, MagicEffectType.AnchoredBlock, MagicEffectType.Duelist, MagicEffectType.ModifyBlockStaminaUse,
            MagicEffectType.ModifyMovementSpeed, MagicEffectType.RemoveSpeedPenalty, MagicEffectType.TravelLight, MagicEffectType.ModifySprintStaminaUse,
            MagicEffectType.ModifyJumpStaminaUse, MagicEffectType.ModifyDodgeStaminaUse, MagicEffectType.DecreaseDodgeCost, MagicEffectType.ModifyNoise,
            MagicEffectType.AddCarryWeight, MagicEffectType.Megingjord, MagicEffectType.CarryWeightForMovementPenalty, MagicEffectType.ReduceWeight,
            MagicEffectType.Weightless, MagicEffectType.IncreaseHeatResistance,
            MagicEffectType.ModifyDamage, MagicEffectType.ModifyPhysicalDamage, MagicEffectType.ModifyElementalDamage, MagicEffectType.AddBluntDamage,
            MagicEffectType.AddSlashingDamage, MagicEffectType.AddPiercingDamage, MagicEffectType.AddFireDamage, MagicEffectType.AddFrostDamage,
            MagicEffectType.AddLightningDamage, MagicEffectType.AddPoisonDamage, MagicEffectType.AddSpiritDamage, MagicEffectType.CoinHoarder,
            MagicEffectType.ConvertPhysicalDamageToLightning, MagicEffectType.IcyWeight, MagicEffectType.DamageBonusFromPlayerWeight,
            MagicEffectType.DamageIncreaseFromMovementPenalty, MagicEffectType.NecroticFire,
            MagicEffectType.AddSwordsSkill, MagicEffectType.AddKnivesSkill, MagicEffectType.AddClubsSkill, MagicEffectType.AddPolearmsSkill,
            MagicEffectType.AddSpearsSkill, MagicEffectType.AddBlockingSkill, MagicEffectType.AddAxesSkill, MagicEffectType.AddBowsSkill,
            MagicEffectType.AddCrossbowsSkill, MagicEffectType.AddUnarmedSkill, MagicEffectType.AddPickaxesSkill, MagicEffectType.AddFishingSkill,
            MagicEffectType.AddElementalMagicSkill, MagicEffectType.AddBloodMagicSkill, MagicEffectType.AddMovementSkills, MagicEffectType.AddCrafterSkills,
            MagicEffectType.IncreaseMeleeSkills, MagicEffectType.IncreaseRangedSkills, MagicEffectType.BlockAsDodgeAsBlock, MagicEffectType.BlockAsWoodCuttingAndPickaxes,
            MagicEffectType.Comfortable,
            // EpicLoot grants it as the game's own Feather fall status, which the status rows list.
            MagicEffectType.FeatherFall,
            // Properties of a single item that its own tooltip shows; they do nothing to the player.
            MagicEffectType.ModifyDurability, MagicEffectType.Indestructible,
            // Both sail-force multipliers combine before the daytime and nighttime speed rows are calculated.
            MagicEffectType.SailingSpeed, MagicEffectType.DaySailingSpeed,
        };

        // Where every other enchantment is listed; any not named here goes to Other effects.
        private static readonly Dictionary<string, Section> Sections = new Dictionary<string, Section>();

        private static readonly string[] CriticalHealth =
        {
            MagicEffectType.ModifyArmorLowHealth, MagicEffectType.ModifyDamageLowHealth, MagicEffectType.ModifyBlockPowerLowHealth,
            MagicEffectType.ModifyParryLowHealth, MagicEffectType.ModifyMovementSpeedLowHealth, MagicEffectType.ModifyHealthRegenLowHealth,
            MagicEffectType.ModifyStaminaRegenLowHealth, MagicEffectType.ModifyEitrRegenLowHealth, MagicEffectType.ModifyAttackSpeedLowHealth,
            MagicEffectType.AvoidDamageTakenLowHealth, MagicEffectType.LifeStealLowHealth,
        };
        private static readonly string[] Daytime =
        {
            MagicEffectType.DayArmor, MagicEffectType.DayHealthRegen, MagicEffectType.DayStaminaRegen, MagicEffectType.IncreaseDamageDuringDaytime,
            MagicEffectType.DayDiscovery, MagicEffectType.DayBlocker,
        };
        private static readonly string[] Nighttime =
        {
            MagicEffectType.DamageReductionAtNight, MagicEffectType.NightStaminaRegenIncrease, MagicEffectType.IncreaseDamageDuringNighttime,
            MagicEffectType.NightCarryWeight, MagicEffectType.NightBlocker,
        };
        private static readonly string[] WhileRested = { MagicEffectType.RestingHealthRegen, MagicEffectType.GainMaxCarryWeightFromRested };

        // What EpicLoot's statuses without text of their own do, by status name, where the row's label doesn't say.
        private static readonly Dictionary<string, string> EpicLootStatusTexts = new Dictionary<string, string>
        {
            ["EL_AssassinCooldown"] = "Your Assassin strike is recharging.",
            ["EL_BonemassPoisonCooldown"] = "Enemies you kill won't burst in venom until this ends.",
            ["EL_EitrBarrier"] = "Part of every hit is taken from your eitr first.",
            ["EL_EitrInfusion"] = "Your hits deal extra damage. Your eitr doesn't regenerate.",
            ["EL_ForestsAidCooldown"] = "Roots won't erupt when you are hit until this ends.",
            ["EL_Frostwalker"] = "Water freezes under your feet, draining your eitr. Crouch to break through.",
            ["EL_IcyRetributionCooldown"] = "You won't release a frost nova when hit until this ends.",
            ["EL_MeteorSummonerCharge"] = "Your weapon hits build charges. At full charge, your next hit calls down a meteor.",
            ["EL_RunningOnEmptyCooldown"] = "You won't burn health for stamina until this ends.",
            ["EL_ShockingChargeIndicator"] = "Your hits build charges. At full charge, your next hit unleashes a lightning shockwave.",
            ["EL_SpiritAnimal"] = "Your bound creature's spirit fights beside you until this ends.",
            ["EL_Vanished"] = "Enemies have lost sight of you. Your next hit from hiding is a sneak attack.",
        };

        private static Page page;
        private static MagicPages shownOn;
        private static List<string> shownRows;
        // One tooltip per status row, in row order.
        private static readonly List<UITooltip> statusTooltips = new List<UITooltip>();
        private static GameObject columns;
        private static float nextRefresh;

        static ActiveEffectsPage()
        {
            void Put(Section section, params string[] types)
            {
                foreach (string type in types) Sections[type] = section;
            }
            Put(Section.Resources, CriticalHealth.Where(type => type.Contains("Regen")).ToArray());
            Put(Section.Resources, MagicEffectType.DayHealthRegen, MagicEffectType.DayStaminaRegen, MagicEffectType.NightStaminaRegenIncrease,
                MagicEffectType.RestingHealthRegen, MagicEffectType.LifeSteal, MagicEffectType.LifeStealLowHealth, MagicEffectType.EitrLeech,
                MagicEffectType.BloodDrinker, MagicEffectType.Bloodlust, MagicEffectType.LifeGainOnHit, MagicEffectType.HealthGainPerXDamageDone,
                MagicEffectType.HealthOnEitrUse, MagicEffectType.StaminaOnKill, MagicEffectType.StaminaReturnFromEitr, MagicEffectType.RunningOnEmpty,
                MagicEffectType.ConsumeEitrFirstForBloodCosts, MagicEffectType.ConvertEitrCostToStaminaCost, MagicEffectType.ReduceEitrCost,
                MagicEffectType.KillsReduceNextBloodCost, MagicEffectType.GainEitrWhenSacrificingHealth, MagicEffectType.Conduit, MagicEffectType.Kindling,
                MagicEffectType.PerfectDodge, MagicEffectType.PerfectDodgeGivesHealth, MagicEffectType.PerfectDodgeGivesStamina,
                MagicEffectType.PerfectDodgeGivesEitr, MagicEffectType.AdrenalineCharge, MagicEffectType.AdrenalineFrostWave,
                MagicEffectType.AdrenalineIncreasesHealthRegen, MagicEffectType.BurningAdrenaline, MagicEffectType.DamageTakenGivesAdrenaline,
                MagicEffectType.EitrUseGivesAdrenaline, MagicEffectType.GainAdrenalineFromHarvesting, MagicEffectType.GainAdrenalineWhenApplyingPoison,
                MagicEffectType.GainAdrenalineWhenSacrificingHealth, MagicEffectType.SummonBatWhenActivatingAdrenaline, MagicEffectType.UseAdrenalineAsStamina,
                MagicEffectType.StormFury, MagicEffectType.Everflow, MagicEffectType.AutoMead, MagicEffectType.InstantMead,
                MagicEffectType.DecreaseMeadCooldown, MagicEffectType.PotionEfficacy, MagicEffectType.DecreaseForsakenCooldown);
            Put(Section.Defense, MagicEffectType.ModifyArmorLowHealth, MagicEffectType.DayArmor, MagicEffectType.DamageReductionAtNight,
                MagicEffectType.ModifyBlockPowerLowHealth, MagicEffectType.ModifyParryLowHealth, MagicEffectType.AvoidDamageTaken,
                MagicEffectType.AvoidDamageTakenLowHealth, MagicEffectType.ReflectDamage, MagicEffectType.EitrShield, MagicEffectType.Warding,
                MagicEffectType.ElementalWarding, MagicEffectType.Coinplated, MagicEffectType.Immovable, MagicEffectType.BloodStaggerBlock,
                MagicEffectType.BloodBaseBlock, MagicEffectType.LifeGainOnBlock, MagicEffectType.StaminaGainOnBlock, MagicEffectType.EitrGainOnBlock,
                MagicEffectType.LuckyBlock, MagicEffectType.StaggerOnBlock, MagicEffectType.PhysToFireOnBlock, MagicEffectType.PhysToFrostOnBlock,
                MagicEffectType.PhysToPoisonOnBlock, MagicEffectType.PhysToLightningOnBlock, MagicEffectType.EitrWeave, MagicEffectType.OffSetAttack,
                MagicEffectType.Bulwark, MagicEffectType.Undying, MagicEffectType.ReduceFallDamage, MagicEffectType.RollCleanse,
                MagicEffectType.ForestsAid, MagicEffectType.IcyRetribution, MagicEffectType.Waterproof, MagicEffectType.Warmth, MagicEffectType.Paralyze);
            Put(Section.Movement, MagicEffectType.ModifyMovementSpeedLowHealth, MagicEffectType.NightCarryWeight, MagicEffectType.GainMaxCarryWeightFromRested,
                MagicEffectType.DoubleJump, MagicEffectType.StormRider, MagicEffectType.BurningSpeed, MagicEffectType.PerfectDodgeGivesSpeed,
                MagicEffectType.Trailblazer);
            Put(Section.Attacks, MagicEffectType.ModifyDamageLowHealth, MagicEffectType.IncreaseDamageDuringDaytime, MagicEffectType.IncreaseDamageDuringNighttime,
                MagicEffectType.ModifyAttackSpeed, MagicEffectType.ModifyAttackSpeedLowHealth, MagicEffectType.ModifyBackstab,
                MagicEffectType.ModifyStaggerDamage, MagicEffectType.ModifyStaggerDuration, MagicEffectType.StaggerOnDamageTaken,
                MagicEffectType.ChanceDoubleDamage, MagicEffectType.ChanceToCritOnHit, MagicEffectType.Executioner, MagicEffectType.Opportunist,
                MagicEffectType.Slow, MagicEffectType.ExplosiveArrows, MagicEffectType.QuickDraw, MagicEffectType.ModifyDrawStaminaUse,
                MagicEffectType.ModifyProjectileSpeed, MagicEffectType.ModifyMagicFireRate, MagicEffectType.ModifyFireRate, MagicEffectType.AmmoConservation,
                MagicEffectType.TripleBowShot, MagicEffectType.DoubleMagicShot, MagicEffectType.ChainLightning, MagicEffectType.FrostDamageAOE,
                MagicEffectType.StrikeCausesLightning, MagicEffectType.ShockingCharge, MagicEffectType.MeteorSummoner, MagicEffectType.CorpseRot,
                MagicEffectType.PoisonToTrueDamage, MagicEffectType.IncreaseAllPoisonDamageDone, MagicEffectType.EitrImbueAttack,
                MagicEffectType.Throwable, MagicEffectType.RecallWeapon, MagicEffectType.Apportation, MagicEffectType.HeadHunter,
                MagicEffectType.DodgeBuff, MagicEffectType.Bloodrage, MagicEffectType.SpellSword, MagicEffectType.ModifySummonDamage,
                MagicEffectType.ModifySummonHealth, MagicEffectType.BatteringRam, MagicEffectType.ModifyAttackStaminaUse,
                MagicEffectType.ModifyAttackEitrUse, MagicEffectType.ModifyAttackHealthUse);
            Put(Section.Skills, MagicEffectType.QuickLearner, MagicEffectType.Inspiration, MagicEffectType.IncreaseXPGainFromMovementPenalty,
                MagicEffectType.IncreaseHarvestXPGain, MagicEffectType.IncreaseRangedSkills, MagicEffectType.DayBlocker, MagicEffectType.NightBlocker);
            Put(Section.Utility, MagicEffectType.Luck, MagicEffectType.LuckyLoot, MagicEffectType.LuckyCraft, MagicEffectType.LuckWhileFishing,
                MagicEffectType.Riches, MagicEffectType.IncreaseMiningDrop, MagicEffectType.IncreaseTreeDrop, MagicEffectType.IncreaseHarvestDamage,
                MagicEffectType.BountifulHarvest, MagicEffectType.Mercenary, MagicEffectType.Wager, MagicEffectType.ModifyPickupRange,
                MagicEffectType.ModifyBuildDistance, MagicEffectType.ModifyDiscoveryRadius, MagicEffectType.DayDiscovery, MagicEffectType.ModifyWispRange,
                MagicEffectType.FreeBuild, MagicEffectType.Glowing, MagicEffectType.DvergerCirclet, MagicEffectType.Wishbone, MagicEffectType.Andvaranaut,
                MagicEffectType.ReduceFishingStaminaCost);
            Put(Section.Resources, MagicEffectType.LifeAfterKill, MagicEffectType.LifeSiphon, MagicEffectType.GiantsBlood);
            Put(Section.Defense, MagicEffectType.ReduceStaggerTaken, MagicEffectType.EitrBarrier, MagicEffectType.Vanish);
            Put(Section.Movement, MagicEffectType.ModifyGrapplePullSpeed, MagicEffectType.ModifyGrappleFlightTime,
                MagicEffectType.Frostwalker, MagicEffectType.Hexen);
            Put(Section.Attacks, MagicEffectType.CloseQuarter, MagicEffectType.Artillery, MagicEffectType.Assassin,
                MagicEffectType.CoilingVenom, MagicEffectType.ConjuredArrows, MagicEffectType.Earthshaker,
                MagicEffectType.EitrInfusion, MagicEffectType.ElementalArchery, MagicEffectType.Firebrand,
                MagicEffectType.HelsHarvest, MagicEffectType.KineticQuake, MagicEffectType.NineSteps,
                MagicEffectType.OverwhelmingLaunch, MagicEffectType.PiercingBolts, MagicEffectType.QuarryMark,
                MagicEffectType.ShieldThrow, MagicEffectType.Sniper, MagicEffectType.SpiritAnimal);
            Put(Section.Utility, MagicEffectType.GreenThumb, MagicEffectType.Prosperity);
        }

        private sealed class Page : MagicTextInfo
        {
            internal Page() : base(Localization.instance.Localize("$inventory_activeeffects")) { }

            public override void Build(MagicPages pages)
            {
                shownOn = pages;
                shownRows = null;
                pages.TitleElement.SetFontSize(TitleFontSize);
                pages.TitleElement.SetSize(pages.MinWidth, TitleHeight);
                if (Player.m_localPlayer == null) return;
                var tooltips = new List<(string Topic, string Text)>();
                Fill(pages, RowsWithTooltips(Player.m_localPlayer, tooltips), tooltips);
            }
        }

        // After EpicLoot adds its pages: drop both effect pages and put this one first.
        [HarmonyPatch(typeof(TextsDialog), "UpdateTextsList"), HarmonyPostfix, HarmonyAfter(PluginIds.EpicLoot)]
        private static void ReplacePages(TextsDialog __instance)
        {
            if (Player.m_localPlayer == null || MagicPages.instance == null) return;
            var texts = Texts(__instance);
            string native = Localization.instance.Localize("$inventory_activeeffects");
            texts.RemoveAll(text => text == MagicPages.instance.MagicEffectsPage || !(text is MagicTextInfo) && text.m_topic == native);
            texts.Insert(0, page ?? (page = new Page()));
        }

        [HarmonyPatch(typeof(MagicPages), nameof(MagicPages.Reset)), HarmonyPostfix]
        private static void Hidden(MagicPages __instance)
        {
            if (shownOn != __instance) return;
            shownOn = null;
            shownRows = null;
            statusTooltips.Clear();
            if (columns != null) UnityEngine.Object.Destroy(columns);
            columns = null;
        }

        [HarmonyPatch(typeof(MagicPages), nameof(MagicPages.OnSelectText)), HarmonyPostfix]
        private static void Opened(MagicPages __instance, MagicTextInfo text)
        {
            if (text != page || shownOn != __instance) return;
            var scroll = ListScroll(__instance.MagicPagesTextArea);
            var title = ElementObject(__instance.TitleElement);
            // EpicLoot destroys the previous page at the end of this frame. Its old rows, title and columns
            // must stop participating in layout before the new page's first visible position is calculated.
            foreach (Transform child in scroll.content)
                if (child.gameObject != title && child.gameObject != columns &&
                    !(child.GetComponent<LayoutElement>()?.ignoreLayout ?? false)) child.gameObject.SetActive(false);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
            scroll.StopMovement();
            scroll.verticalNormalizedPosition = 1f;
        }

        // Updates the open page in place, keeping the scroll position and the search, and rebuilds it only when rows
        // come or go.
        [HarmonyPatch(typeof(MagicPages), nameof(MagicPages.Update)), HarmonyPostfix]
        private static void Refresh(MagicPages __instance)
        {
            if (shownOn != __instance || shownRows == null || Time.unscaledTime < nextRefresh || Player.m_localPlayer == null) return;
            var tooltips = new List<(string Topic, string Text)>();
            var rows = RowsWithTooltips(Player.m_localPlayer, tooltips);
            var groups = __instance.MagicPagesTextArea.Elements;
            if (rows.Count == shownRows.Count && groups.Count == rows.Count && tooltips.Count == statusTooltips.Count)
            {
                for (int index = 0; index < rows.Count; index++)
                    if (rows[index] != shownRows[index]) SetText((MagicTextElement)groups[index].Title, Shown(rows[index]));
                for (int index = 0; index < tooltips.Count; index++)
                {
                    statusTooltips[index].Set(tooltips[index].Topic, tooltips[index].Text);
                    // Set would leave an open window standing empty.
                    if (tooltips[index].Text.Length == 0 && ShownTooltip() == statusTooltips[index]) UITooltip.HideTooltip();
                }
                shownRows = rows;
                nextRefresh = Time.unscaledTime + RefreshSeconds;
            }
            else
            {
                __instance.MagicPagesTextArea.Clear();
                Fill(__instance, rows, tooltips);
            }
            __instance.MagicPagesTextArea.ResizeOverlay();
            __instance.OnSearch(__instance.Search.Input.text);
        }

        private static void Fill(MagicPages pages, List<string> rows, List<(string Topic, string Text)> tooltips)
        {
            foreach (string row in rows) pages.MagicPagesTextArea.Add(Shown(row));
            Arrange(pages, rows, tooltips);
            shownRows = rows;
            nextRefresh = Time.unscaledTime + RefreshSeconds;
        }

        // EpicLoot gives every line its own element at least 35 units tall in one list. Columns of their own size each line
        // to its text instead and split the page between sections, so no section is cut in two.
        private static void Arrange(MagicPages pages, List<string> rows, List<(string Topic, string Text)> tooltips)
        {
            if (columns == null)
            {
                columns = new GameObject("Active effects columns", typeof(RectTransform));
                columns.transform.SetParent(ListLayout(pages.MagicPagesTextArea).transform, false);
                // Growing downward, so a new row doesn't shift the rows above it out from under the pointer.
                ((RectTransform)columns.transform).pivot = new Vector2(.5f, 1f);
                var across = columns.AddComponent<HorizontalLayoutGroup>();
                across.spacing = ColumnSpacing;
                across.childControlWidth = across.childForceExpandWidth = across.childControlHeight = true;
                across.childForceExpandHeight = false;
                columns.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                for (int index = 0; index < 2; index++)
                {
                    var column = new GameObject("Column", typeof(RectTransform));
                    column.transform.SetParent(columns.transform, false);
                    // Equal halves, whatever the longest line in each, so the columns do not shift as values change.
                    var size = column.AddComponent<LayoutElement>();
                    size.minWidth = size.preferredWidth = 0f;
                    size.flexibleWidth = 1f;
                    var down = column.AddComponent<VerticalLayoutGroup>();
                    down.spacing = RowSpacing;
                    down.childAlignment = TextAnchor.UpperLeft;
                    down.childControlWidth = down.childForceExpandWidth = down.childControlHeight = true;
                    down.childForceExpandHeight = false;
                }
            }
            columns.transform.SetAsLastSibling();
            // The second column starts at the section boundary that leaves the two closest in length.
            int split = rows.Count;
            for (int index = 1; index < rows.Count; index++)
                if (IsHeading(rows[index]) && Math.Max(index, rows.Count - index) < Math.Max(split, rows.Count - split))
                    split = index;
            var elements = pages.MagicPagesTextArea.Elements;
            for (int index = 0; index < elements.Count; index++)
            {
                var element = (MagicTextElement)elements[index].Title;
                element.SetFontSize(RowFontSize);
                element.SetParent(columns.transform.GetChild(index < split ? 0 : 1));
                if (!IsHeading(rows[index])) continue;
                element.SetAlignment(TextAnchor.LowerLeft);
                ElementObject(element).AddComponent<LayoutElement>().minHeight = HeadingHeight;
            }
            // The status rows follow the first heading.
            statusTooltips.Clear();
            var window = InventoryGui.instance.m_playerGrid.m_elementPrefab.GetComponent<UITooltip>().m_tooltipPrefab;
            for (int index = 0; index < tooltips.Count; index++)
            {
                var row = ElementObject((MagicTextElement)elements[index + 1].Title);
                row.GetComponent<TMP_Text>().raycastTarget = true;
                var tooltip = row.AddComponent<UITooltip>();
                tooltip.m_tooltipPrefab = window;
                tooltip.Set(tooltips[index].Topic, tooltips[index].Text);
                statusTooltips.Add(tooltip);
            }
        }

        // A line that wraps in its column continues indented, so it does not read as a new line.
        private static string Shown(string row) => IsHeading(row) ? row : "<indent=1em><line-indent=-1em>" + row;

        private static bool IsHeading(string row) => row.StartsWith($"<size={HeadingFontSize}>", StringComparison.Ordinal);

        // The page's rows, headings included, in rich text.
        internal static List<string> Rows(Player player) => RowsWithTooltips(player, new List<(string Topic, string Text)>());

        // Also the tooltip of each status row, in row order.
        private static List<string> RowsWithTooltips(Player player, List<(string Topic, string Text)> tooltips)
        {
            // The game adds a skill the character has never used at level 0 when anything reads it, and the skills
            // screen would then list it, so the page removes the ones its readings add.
            var learned = SkillData(player.GetSkills());
            var known = new HashSet<Skills.SkillType>(learned.Keys);
            try { return Build(player, known, tooltips); }
            finally
            {
                foreach (var skill in learned.Keys.Where(skill => !known.Contains(skill)).ToList()) learned.Remove(skill);
            }
        }

        private static List<string> Build(Player player, HashSet<Skills.SkillType> known, List<(string Topic, string Text)> tooltips)
        {
            var rows = new List<string>();
            var listed = new Dictionary<Section, List<string>>();
            foreach (var group in player.GetAllActiveMagicEffects().GroupBy(effect => effect.EffectType).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                if (Included.Contains(group.Key) || !MagicItemEffectDefinitions.TryGet(group.Key, out var definition)) continue;
                string text = Localization.instance.Localize(MagicItem.GetEffectText(definition, player.GetTotalActiveMagicEffectValue(group.Key)));
                bool? active = Condition(player, group.Key);
                if (active == true) text += Muted(" (active now)");
                else if (active == false) text = Muted(text + " (not now)");
                var section = Sections.TryGetValue(group.Key, out var mapped) ? mapped : Section.Other;
                if (!listed.TryGetValue(section, out var lines)) listed[section] = lines = new List<string>();
                lines.Add(text);
            }

            var statuses = new List<string>();
            StatusRows(player, statuses, tooltips);
            if (statuses.Count > 0) { rows.Add(Title("Status effects")); rows.AddRange(statuses); }
            foreach (Section section in Enum.GetValues(typeof(Section)))
            {
                var computed = new List<string>();
                switch (section)
                {
                    case Section.Resources: ResourceRows(player, computed); break;
                    case Section.Defense: DefenseRows(player, computed); break;
                    case Section.Movement: MovementRows(player, computed); break;
                    case Section.Attacks: AttackRows(player, computed); break;
                    case Section.Skills: SkillRows(player, known, computed); break;
                    case Section.Utility: UtilityRows(player, computed); break;
                }
                if (listed.TryGetValue(section, out var lines)) computed.AddRange(lines);
                if (computed.Count == 0) continue;
                rows.Add(Title(SectionNames[(int)section]));
                rows.AddRange(computed);
            }
            return rows;
        }

        private static void StatusRows(Player player, List<string> rows, List<(string Topic, string Text)> tooltips)
        {
            // The statuses the HUD shows. Their rows name them; hovering one says what it does.
            var effects = new List<StatusEffect>();
            player.GetSEMan().GetHUDStatusEffects(effects);
            foreach (var effect in effects)
            {
                string name = Localization.instance.Localize(effect.m_name);
                if (string.IsNullOrWhiteSpace(name)) continue;
                string detail = effect.GetIconText();
                rows.Add(string.IsNullOrEmpty(detail) ? name : Row(name, detail));
                tooltips.Add(Tooltip(name, Description(effect)));
            }
            foreach (var food in player.GetFoods())
            {
                string name = Localization.instance.Localize(food.m_item.m_shared.m_name);
                rows.Add(Row(name, Duration(food.m_time)));
                tooltips.Add(Tooltip(name, FoodText(food)));
            }
            player.GetGuardianPowerHUD(out var power, out float cooldown);
            if (power != null)
            {
                string name = Localization.instance.Localize(power.m_name);
                rows.Add(Row("Forsaken power: " + name, cooldown > 0f ? "ready in " + Duration(cooldown) : "ready"));
                tooltips.Add(Tooltip(name, PowerText(player, power)));
            }
        }

        // No text means no tooltip, so a window never just repeats the row's name.
        private static (string Topic, string Text) Tooltip(string name, string text) => text.Length == 0 ? ("", "") : (name, text);

        // What the game's statuses do, by status name, where their own text only sets a mood or describes something else.
        private static string GameStatusText(StatusEffect effect)
        {
            switch (effect.name)
            {
                case "Burning": return "Fire damage over time. Getting wet puts it out sooner.";
                case "CampFire": return "Eases the cold. Sit or take shelter here to rest.";
                case "Demister":
                    var ball = WispBall((SE_Demister)effect);
                    var wisp = ball ? ball.GetComponentInChildren<Demister>() : null;
                    return wisp ? $"Clears the mist within {wisp.m_forceField.endRange.ToString("0.#", Numbers)} m of you." : "Clears the mist around you.";
                case "EL_FrostWave":
                case "Frost": return $"Slows you by about {Mathf.RoundToInt((1f - ((SE_Frost)effect).m_minSpeedFactor) * 100f)}% at first, easing as it wears off. Any frost resistance prevents it.";
                case "Harpooned": return "A line ties you to whoever harpooned you and drags you back if you pull away.";
                case "Paralyze": return "You can't move until this ends.";
                case "Potion_BugRepellent": return "Deathsquitos flee from you instead of attacking.";
                case "Potion_tamer": return "Animals near you tame twice as fast.";
                case "Potion_TrollPheromones": return $"Trolls spawn more often where you are, with at least {Plugin.LovePotionMinimumTrollStars.Value} stars.";
                case "Smoked": return "You lose 2 health every second until you leave the smoke.";
                case "Spirit": return "Spirit damage over time.";
                default: return null;
            }
        }

        private static string Description(StatusEffect effect)
        {
            string text = effect.GetTooltipString(), ownText = GameStatusText(effect);
            // Any rows the game lists below its own text stay.
            if (ownText != null) text = ownText + text.Substring(effect.m_tooltip.Length);
            text = Localization.instance.Localize(StatusEffectDurationUnits.WithSeconds(text)).Trim();
            return text.Length == 0 && EpicLootStatusTexts.TryGetValue(effect.name, out string own) ? own : text;
        }

        private static string Detail(string label, string value) => $"{label}: <color={ValueColor}>{value}</color>";

        // Not the item's tooltip: its weight means nothing for food already eaten, and the row shows the time left.
        private static string FoodText(Player.Food food)
        {
            var lines = new List<string>();
            if (food.m_health > 0f) lines.Add(Detail("Health", Number(food.m_health, "0")));
            if (food.m_stamina > 0f) lines.Add(Detail("Stamina", Number(food.m_stamina, "0")));
            if (food.m_eitr > 0f) lines.Add(Detail("Eitr", Number(food.m_eitr, "0")));
            if (food.m_item.m_shared.m_foodRegen > 0f) lines.Add(Detail("Healing", Number(food.m_item.m_shared.m_foodRegen) + " every 10 s"));
            return string.Join("\n", lines);
        }

        private static string PowerText(Player player, StatusEffect power)
        {
            var lines = new List<string>();
            string description = Description(power);
            if (description.Length > 0) lines.Add(description);
            // EpicLoot shortens the recharge only as the power is used, so the power's own time doesn't include it.
            float reduction = player.GetTotalActiveMagicEffectValue(MagicEffectType.DecreaseForsakenCooldown, .01f);
            if (power.m_cooldown > 0f) lines.Add(Detail("Recharge", Duration(power.m_cooldown * (1f - reduction))));
            return string.Join("\n", lines);
        }

        private static void ResourceRows(Player player, List<string> rows)
        {
            rows.Add(Row("Health", Number(player.GetHealth(), "0") + " / " + Number(player.GetMaxHealth(), "0")));
            var seman = player.GetSEMan();
            float healthMultiplier = 1f;
            seman.ModifyHealthRegen(ref healthMultiplier);
            // The game heals every 10 seconds from food, EpicLoot's flat regeneration only with food, times the multiplier.
            float food = player.GetFoods().Sum(entry => entry.m_item.m_shared.m_foodRegen);
            float heal = food > 0f ? (food + player.GetTotalActiveMagicEffectValue(MagicEffectType.AddHealthRegen)) * healthMultiplier : 0f;
            if (food > 0f) rows.Add(Row("Health regeneration", Number(heal) + " every 10 s" +
                (Mathf.Approximately(healthMultiplier, 1f) ? "" : " (" + Change(healthMultiplier) + ")")));
            float critical = ModifyWithLowHealth.GetLowHealthPercentage(player);
            if (!Mathf.Approximately(critical, ModifyWithLowHealth.LowHealthDefaultThreshold) || CriticalHealth.Any(type => player.HasActiveMagicEffect(type)))
                rows.Add(Row("Health Critical", "at " + Number(Mathf.Min(critical, 1f) * 100f) + "% or less" + (ModifyWithLowHealth.PlayerHasLowHealth(player) ? " (now)" : "")));
            rows.Add(Row("Stamina", Number(player.GetStamina(), "0") + " / " + Number(player.GetMaxStamina(), "0")));
            float staminaMultiplier = 1f;
            seman.ModifyStaminaRegen(ref staminaMultiplier);
            if (!Mathf.Approximately(staminaMultiplier, 1f)) rows.Add(Row("Stamina regeneration", Change(staminaMultiplier)));
            float delay = StaminaRegenDelay.EffectiveDelay(player.m_staminaRegenDelay, player);
            if (!Mathf.Approximately(delay, player.m_staminaRegenDelay)) rows.Add(Row("Stamina regeneration delay", Number(delay, "0.##") + " s"));
            float eitrMultiplier = 1f;
            seman.ModifyEitrRegen(ref eitrMultiplier);
            if (player.GetMaxEitr() > 0f)
            {
                rows.Add(Row("Eitr", Number(player.GetEitr(), "0") + " / " + Number(player.GetMaxEitr(), "0")));
                if (!Mathf.Approximately(eitrMultiplier, 1f)) rows.Add(Row("Eitr regeneration", Change(eitrMultiplier)));
            }
            if (player.GetMaxAdrenaline() > 0f)
                rows.Add(Row("Adrenaline", Number(player.GetAdrenaline(), "0") + " / " + Number(player.GetMaxAdrenaline(), "0")));
        }

        private static void DefenseRows(Player player, List<string> rows)
        {
            rows.Add(Row("Armor", Number(player.GetBodyArmor(), "0.#")));
            // The share of a hit of each type that gets through, before armor: incoming physical conversion, EpicLoot's
            // capped resistances, the night reduction and the game's resistances. The character's own values, without the
            // world's combat difficulty or the players nearby. Types taken alike share a row.
            var modifiers = player.GetDamageModifiers();
            var alike = new List<(float Taken, List<string> Names)>();
            foreach (var (type, name) in DamageTypes)
            {
                var hit = new HitData();
                AddDamage(ref hit.m_damage, type, 100f);
                IncomingPhysicalConversion.ModifyIncoming(player, hit);
                ModifyResistance.ModifyIncoming(player, hit);
                DamageReductionAtNight.ModifyIncoming(player, hit);
                hit.ApplyResistance(modifiers, out _);
                float taken = hit.GetTotalDamage() / 100f;
                int index = alike.FindIndex(entry => Mathf.Abs(entry.Taken - taken) < 0.0005f);
                if (index < 0) alike.Add((taken, new List<string> { name }));
                else alike[index].Names.Add(name.ToLowerInvariant());
            }
            foreach (var (taken, names) in alike)
            {
                if (Mathf.Approximately(taken, 1f)) continue;
                string types = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
                rows.Add(Row(types + " damage taken", taken <= 0f ? "immune" : Change(taken)));
            }

            var blocker = BlockedHits.CurrentBlocker(player);
            if (blocker != null)
            {
                float skill = player.GetSkillFactor(Skills.SkillType.Blocking);
                string name = Localization.instance.Localize(blocker.m_shared.m_name);
                rows.Add(Row("Block armor", Number(blocker.GetBlockPower(skill), "0.#") + " (" + name + ")"));
                rows.Add(Row("Block force", Number(blocker.GetDeflectionForce(), "0.#")));
                if (blocker.m_shared.m_timedBlockBonus > 1f)
                {
                    // Worked out as at the start of a block, as the hit would meet it.
                    float timer = BlockTimer(player);
                    float parry, parryForce;
                    try
                    {
                        BlockTimer(player) = 0f;
                        parry = blocker.GetBlockPower(skill) * blocker.m_shared.m_timedBlockBonus;
                        player.GetSEMan().ModifyTimedBlockBonus(ref parry);
                        parryForce = blocker.GetDeflectionForce();
                    }
                    finally { BlockTimer(player) = timer; }
                    rows.Add(Row("Parry armor", Number(parry, "0.#")));
                    if (!Mathf.Approximately(parryForce, blocker.GetDeflectionForce())) rows.Add(Row("Parry force", Number(parryForce, "0.#")));
                    rows.Add(Row("Parry window", Number(ParryWindow.Seconds(player) + player.GetTotalActiveMagicEffectValue(MagicEffectType.ModifyParryWindow) / 1000f, "0.###") + " s"));
                    float parryStamina = BlockStamina(player, player.m_perfectBlockStaminaDrain);
                    if (parryStamina != 0f) rows.Add(Row("Parry stamina", Number(parryStamina)));
                }
                // A held block spends this for a hit that uses all the block armor, and a share of it for a smaller hit.
                rows.Add(Row("Block stamina", "up to " + Number(BlockStamina(player, player.m_blockStaminaDrain)) + " per hit"));
            }
            rows.Add(Row("Stagger limit", Number(player.GetMaxHealth() * player.m_staggerDamageFactor, "0")));
            float stagger = 1f;
            player.GetSEMan().ModifyStagger(1f, ref stagger);
            if (!Mathf.Approximately(stagger, 1f)) rows.Add(Row("Stagger taken", Change(stagger)));
        }

        private static void MovementRows(Player player, List<string> rows)
        {
            var seman = player.GetSEMan();
            float jog = player.m_speed * JogFactor(player);
            seman.ApplyStatusEffectSpeedMods(ref jog, Vector3.zero);
            if (!Mathf.Approximately(jog, player.m_speed)) rows.Add(Row("Movement speed", Change(jog / player.m_speed)));
            float run = player.m_runSpeed * RunFactor(player);
            seman.ApplyStatusEffectSpeedMods(ref run, Vector3.zero);
            if (!Mathf.Approximately(run, player.m_runSpeed)) rows.Add(Row("Running speed", Change(run / player.m_runSpeed)));
            var jump = new Vector3(0f, player.m_jumpForce, 0f);
            seman.ApplyStatusEffectJumpMods(ref jump);
            if (!Mathf.Approximately(jump.y, player.m_jumpForce)) rows.Add(Row("Jump strength", Change(jump.y / player.m_jumpForce)));
            rows.Add(Row("Carried weight", Number(player.GetInventory().GetTotalWeight(), "0.#") + " / " + Number(MaxCarryWeight(player), "0.#")));

            var skills = player.GetSkills();
            float movement = player.GetEquipmentMovementModifier();
            float running = player.m_runStaminaDrain * Mathf.Lerp(1f, 0.5f, skills.GetSkillFactor(Skills.SkillType.Run));
            running -= running * movement;
            running += running * player.GetEquipmentRunStaminaModifier();
            seman.ModifyRunStaminaDrain(running, ref running, Vector3.zero);
            rows.Add(Row("Running stamina", Number(running) + " per second"));
            float jumping = player.m_jumpStaminaUsage - player.m_jumpStaminaUsage * movement + player.m_jumpStaminaUsage * player.GetEquipmentJumpStaminaModifier();
            seman.ModifyJumpStaminaUsage(jumping, ref jumping);
            rows.Add(Row("Jump stamina", Number(jumping)));
            rows.Add(Row("Dodge stamina", Number(DodgeStamina(player))));
            float swimming = Mathf.Lerp(player.m_swimStaminaDrainMinSkill, player.m_swimStaminaDrainMaxSkill, skills.GetSkillFactor(Skills.SkillType.Swim));
            swimming += swimming * player.GetEquipmentSwimStaminaModifier();
            seman.ModifySwimStaminaUsage(swimming, ref swimming);
            rows.Add(Row("Swimming stamina", Number(swimming) + " per second"));
            float sneaking = player.m_sneakStaminaDrain * Mathf.Lerp(1f, 0.25f, Mathf.Pow(skills.GetSkillFactor(Skills.SkillType.Sneak), 0.5f));
            sneaking += sneaking * player.GetEquipmentSneakStaminaModifier();
            seman.ModifySneakStaminaUsage(sneaking, ref sneaking);
            rows.Add(Row("Sneaking stamina", Number(sneaking) + " per second"));
            float noise = 1f;
            seman.ModifyNoise(1f, ref noise);
            if (!Mathf.Approximately(noise, 1f)) rows.Add(Row("Noise", Change(noise)));
            float stealth = 1f;
            seman.ModifyStealth(1f, ref stealth);
            if (!Mathf.Approximately(stealth, 1f)) rows.Add(Row("Stealth", Change(stealth)));
            float heat = player.GetEquipmentHeatResistanceModifier();
            if (heat != 0f) rows.Add(Row("Heat resistance", Change(1f + heat)));
        }

        private static void AttackRows(Player player, List<string> rows)
        {
            var weapon = player.GetCurrentWeapon();
            if (weapon == null) return;
            var previousItem = WeaponTooltipRange.Item;
            var previousHands = HandOverride.Active;
            try
            {
                WeaponTooltipRange.Item = weapon;
                WeaponTooltip.AsIfEquipped(weapon);
                var range = WeaponDamagePreview.Range(player, weapon, out var ammo, out bool conjured);
                var parts = new List<string>();
                foreach (var (type, name) in DamageTypes)
                {
                    float low = Damage(range.Low, type), high = Damage(range.High, type);
                    if (low > 0f || high > 0f) parts.Add("<nobr>" + Number(low, "0") + "-" + Number(high, "0") + " " + name.ToLowerInvariant() + "</nobr>");
                }
                string weaponName = Localization.instance.Localize(weapon.m_shared.m_name) + (conjured ? " with conjured arrows" :
                    ammo != null ? " with " + Localization.instance.Localize(ammo.m_shared.m_name) : "");
                rows.Add(Row("Hit damage" + WeaponDamagePreview.PerProjectile(player, weapon), (parts.Count > 0 ? string.Join(", ", parts) : "none") + " (" + weaponName + ")"));
            }
            finally { HandOverride.Active = previousHands; WeaponTooltipRange.Item = previousItem; }
        }

        // Skills raised or lowered by equipment and status effects, including skills the character has not used yet.
        private static void SkillRows(Player player, HashSet<Skills.SkillType> known, List<string> rows)
        {
            var skills = player.GetSkills();
            var learned = SkillData(skills);
            var shown = new List<(string Name, string Value)>();
            foreach (var skill in skills.m_skills.Select(definition => definition.m_skill).Union(known).Distinct())
            {
                float level = known.Contains(skill) ? learned[skill].m_level : 0f;
                // Gameplay reads the skill factor, which carries the game's and EpicLoot's bonuses.
                float effective = skills.GetSkillFactor(skill) * 100f;
                float bonus = effective - Mathf.Min(Mathf.Floor(level), 100f);
                if (Mathf.Abs(bonus) < 0.5f) continue;
                shown.Add((Localization.instance.Localize("$skill_" + skill.ToString().ToLowerInvariant()),
                    Number(effective, "0") + " (" + (bonus > 0f ? "+" : "") + Number(bonus, "0") + ")"));
            }
            foreach (var skill in shown.OrderBy(entry => entry.Name, StringComparer.CurrentCulture)) rows.Add(Row(skill.Name, skill.Value));
        }

        private static void UtilityRows(Player player, List<string> rows)
        {
            rows.Add(Row("Comfort", player.GetComfortLevel().ToString(Numbers)));
            float sailingForcePercent = player.GetTotalActiveMagicEffectValue(MagicEffectType.SailingSpeed);
            float daytimeSailingForcePercent = player.GetTotalActiveMagicEffectValue(MagicEffectType.DaySailingSpeed);
            if (sailingForcePercent <= 0f && daytimeSailingForcePercent <= 0f) return;
            void SailingRow(string label, float forcePercent, bool active)
            {
                if (forcePercent == 0f) return;
                string value = "+" + EffectTooltips.SailingSpeedText(forcePercent) + "%";
                rows.Add(active ? Row(label, value) + Muted(" (active now)") : Muted(label + " " + value + " (not now)"));
            }
            bool day = EnvMan.IsDay();
            float daytime = ((1f + sailingForcePercent * .01f) * (1f + daytimeSailingForcePercent * .01f) - 1f) * 100f;
            if (Mathf.Approximately(daytime, sailingForcePercent))
                rows.Add(Row("Helmsman's sailing and rowing speed", "+" + EffectTooltips.SailingSpeedText(sailingForcePercent) + "%"));
            else
            {
                SailingRow("Helmsman's daytime sailing and rowing speed", daytime, day);
                SailingRow("Helmsman's nighttime sailing and rowing speed", sailingForcePercent, !day);
            }
        }

        // As Humanoid.BlockAttack spends it, before the world's stamina setting; a negative cost gives stamina back.
        private static float BlockStamina(Player player, float drain)
        {
            drain += drain * player.GetEquipmentBlockStaminaModifier();
            player.GetSEMan().ModifyBlockStaminaUsage(drain, ref drain, false);
            return drain;
        }

        // The game multiplies the whole limit, mods' additions included, by the world's carry weight setting; the page
        // shows the character's own, with every equipment, status, skill and enchantment bonus.
        private static float MaxCarryWeight(Player player)
        {
            float rate = Game.m_carryWeightRate;
            Game.m_carryWeightRate = 1f;
            try { return player.GetMaxCarryWeight(); }
            finally { Game.m_carryWeightRate = rate; }
        }

        private static bool? Condition(Player player, string type)
        {
            if (CriticalHealth.Contains(type)) return ModifyWithLowHealth.PlayerHasLowHealth(player);
            if (Daytime.Contains(type)) return EnvMan.IsDay();
            if (Nighttime.Contains(type)) return EnvMan.IsNight();
            if (WhileRested.Contains(type)) return player.GetSEMan().HaveStatusEffect(SEMan.s_statusEffectRested);
            return null;
        }

        private static void AddDamage(ref HitData.DamageTypes damage, HitData.DamageType type, float amount)
        {
            switch (type)
            {
                case HitData.DamageType.Blunt: damage.m_blunt += amount; break;
                case HitData.DamageType.Slash: damage.m_slash += amount; break;
                case HitData.DamageType.Pierce: damage.m_pierce += amount; break;
                case HitData.DamageType.Fire: damage.m_fire += amount; break;
                case HitData.DamageType.Frost: damage.m_frost += amount; break;
                case HitData.DamageType.Lightning: damage.m_lightning += amount; break;
                case HitData.DamageType.Poison: damage.m_poison += amount; break;
                case HitData.DamageType.Spirit: damage.m_spirit += amount; break;
            }
        }

        private static float Damage(HitData.DamageTypes damage, HitData.DamageType type)
        {
            switch (type)
            {
                case HitData.DamageType.Blunt: return damage.m_blunt;
                case HitData.DamageType.Slash: return damage.m_slash;
                case HitData.DamageType.Pierce: return damage.m_pierce;
                case HitData.DamageType.Fire: return damage.m_fire;
                case HitData.DamageType.Frost: return damage.m_frost;
                case HitData.DamageType.Lightning: return damage.m_lightning;
                case HitData.DamageType.Poison: return damage.m_poison;
                case HitData.DamageType.Spirit: return damage.m_spirit;
                default: return 0f;
            }
        }

        private static string Title(string text) => $"<size={HeadingFontSize}><color={HeadingColor}>{text}</color></size>";

        private static string Row(string label, string value) => $"{label} <color={ValueColor}>{value}</color>";

        private static string Muted(string text) => $"<color={MutedColor}>{text}</color>";

        private static string Number(float value, string format = "0.#") => value.ToString(format, Numbers);

        // A multiplier as a change: 1.25 is +25%, 0.4 is -60%.
        private static string Change(float multiplier)
        {
            float percent = (multiplier - 1f) * 100f;
            if (Mathf.Abs(percent) < 0.05f) return "0%";
            return (percent > 0f ? "+" : "") + Number(percent) + "%";
        }

        private static string Duration(float seconds)
        {
            var time = TimeSpan.FromSeconds(Mathf.CeilToInt(Mathf.Max(seconds, 0f)));
            return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";
        }
    }
}
