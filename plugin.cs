using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Jotunn.Utils;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // BepInEx plugin IDs of the mods this add-on depends on or integrates with. Every integration relies on its mod's
    // internals, so each needs the exact supported plugin version, as its BepInPlugin attribute reports it.
    internal static class PluginIds
    {
        internal const string EpicLoot = "randyknapp.mods.epicloot";
        internal const string EpicLootVersion = "0.15.6";
        internal const string ServerCharacters = "org.bepinex.plugins.servercharacters";
        internal const string QuickStack = "goldenrevolver.quick_stack_store";
        internal const string EquipmentAndQuickSlots = "randyknapp.mods.equipmentandquickslots";
        internal const string DropThat = "asharppen.valheim.drop_that";
        internal const string StarLevelSystem = "MidnightsFX.StarLevelSystem";
        internal const string DiscordBot = "RustyMods.DiscordBot";
        internal const string DiscordConnector = "nwesterhausen.DiscordConnector";
        internal const string FedoHud = "fedo.hud";
        internal const string Dyrr = "ezomic.valheim.dyrr";
        internal const string AaaCrafting = "Azumatt.AzuAntiArthriticCrafting";
        internal const string SkilledCarryWeight = "Searica.Valheim.SkilledCarryWeight";
        internal const string QuickCart = "Searica.Valheim.QuickCart";
        internal const string AzuCraftyBoxes = "Azumatt.AzuCraftyBoxes";
        internal const string StoreAndCraft = "com.morda.storeandcraft";
        internal const string ComfortMe = "hardwire99.comfortme";
        internal const string AmmoCount = "aedenthorn.AmmoCount";
        internal const string NoSmokeStayLit = "TastyChickenLegs.NoSmokeStayLit";
        internal const string BetterUi = "MK_BetterUI";
        internal const string PlantEasily = "advize.PlantEasily";
        internal const string PlantEverything = "advize.PlantEverything";
        internal const string ItemStacks = "shudnal.ItemStacksItemWeights";
        internal const string Sailing = "org.bepinex.plugins.sailing";
        internal const string BetterWisps = "digitalroot.betterwisps";
        internal const string MultiUserChest = "com.maxsch.valheim.MultiUserChest";
        internal const string FortifySkills = "dethkube.Valheim.FortifySkillsReborn";
        internal const string LogoutTweaks = "com.orianaventure.mod.LogoutTweaks";
        internal const string TameRiding = "uk.ainzworth.valheim.tameridingoverhaul";
        internal const string NetworkPerformance = "MidnightsFX.NetworkPerformanceSystem";
        internal const string FastLink = "Azumatt.FastLink";
        internal const string ShipStats = "Azumatt.ShipStats";

        // The supported versions include the pack's pins and alternatives checked separately, such as StoreAndCraft.
        // Client-only mods are simply absent on the dedicated server.
        private static readonly Dictionary<string, System.Version[]> TestedVersions = new Dictionary<string, System.Version[]>
        {
            { EpicLoot, new[] { new System.Version(EpicLootVersion) } },
            { ServerCharacters, new[] { new System.Version("1.4.17") } },
            { QuickStack, new[] { new System.Version("1.4.16") } },
            { EquipmentAndQuickSlots, new[] { new System.Version("3.2.1") } },
            { DropThat, new[] { new System.Version("3.1.7") } },
            { StarLevelSystem, new[] { new System.Version("1.32.0") } },
            { DiscordBot, new[] { new System.Version("1.4.11") } },
            { DiscordConnector, new[] { new System.Version("3.2.0") } },
            { FedoHud, new[] { new System.Version("1.1.2") } },
            { Dyrr, new[] { new System.Version("1.4.2") } },
            { AaaCrafting, new[] { new System.Version("2.1.12") } },
            { SkilledCarryWeight, new[] { new System.Version("1.6.1") } },
            { QuickCart, new[] { new System.Version("1.0.1") } },
            { AzuCraftyBoxes, new[] { new System.Version("1.8.28") } },
            { ComfortMe, new[] { new System.Version("1.3.1") } },
            { AmmoCount, new[] { new System.Version("0.5.1") } },
            { NoSmokeStayLit, new[] { new System.Version("2.4.4") } },
            { BetterUi, new[] { new System.Version("2.5.14") } },
            { PlantEasily, new[] { new System.Version("2.3.1") } },
            { PlantEverything, new[] { new System.Version("1.21.4") } },
            { ItemStacks, new[] { new System.Version("1.1.1") } },
            { Sailing, new[] { new System.Version("1.1.12") } },
            { BetterWisps, new[] { new System.Version("1.0.46") } },
            { MultiUserChest, new[] { new System.Version("0.6.2") } },
            { FortifySkills, new[] { new System.Version("1.7.1") } },
            { LogoutTweaks, new[] { new System.Version("1.0.0") } },
            { StoreAndCraft, new[] { new System.Version("1.6.0") } },
            { TameRiding, new[] { new System.Version("0.1.20") } },
            { FastLink, new[] { new System.Version("1.5.3") } },
            { ShipStats, new[] { new System.Version("1.0.8") } },
        };
        private static readonly HashSet<string> installed = new HashSet<string>(), available = new HashSet<string>();
        private static readonly Dictionary<string, string> unavailable = new Dictionary<string, string>();

        internal static void Inspect()
        {
            installed.Clear(); available.Clear(); unavailable.Clear();
            foreach (var pair in TestedVersions)
            {
                if (!Chainloader.PluginInfos.TryGetValue(pair.Key, out var plugin)) continue;
                installed.Add(pair.Key);
                if (plugin.Instance != null && pair.Value.Contains(plugin.Metadata.Version)) available.Add(pair.Key);
                else unavailable[pair.Key] = plugin.Instance == null ? plugin.Metadata.Name + " did not initialize."
                    : plugin.Metadata.Name + " " + plugin.Metadata.Version + " is not in the tested versions: " + string.Join(", ", pair.Value.Select(version => version.ToString())) + ".";
            }
        }
        internal static bool Installed(string id) => installed.Contains(id);
        internal static string UnavailableReason(string id) => unavailable.TryGetValue(id, out var reason) ? reason : null;
        internal static void DisableIntegration(string id, string reason) { available.Remove(id); unavailable[id] = reason; }
        internal static bool TryGet(string id, out PluginInfo plugin)
        {
            bool present = Chainloader.PluginInfos.TryGetValue(id, out plugin);
            if (id == EpicLoot && present && !available.Contains(id)) throw new InvalidOperationException(UnavailableReason(id));
            return present && available.Contains(id);
        }

    }

    [BepInPlugin(Id, "EpicLoot Progression Fix", Version)]
    [BepInDependency(PluginIds.EpicLoot, PluginIds.EpicLootVersion)]
    [BepInDependency("tenemo.WorldPerformance", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [BepInDependency(PluginIds.QuickStack, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.EquipmentAndQuickSlots, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.ServerCharacters, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.DropThat, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.FastLink, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.StarLevelSystem, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.DiscordBot, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.DiscordConnector, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.FedoHud, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.Dyrr, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.AaaCrafting, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.SkilledCarryWeight, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.QuickCart, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.AzuCraftyBoxes, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.StoreAndCraft, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.ComfortMe, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.AmmoCount, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.LogoutTweaks, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.NoSmokeStayLit, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.BetterUi, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.PlantEasily, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.PlantEverything, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.ItemStacks, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.MultiUserChest, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.Sailing, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.BetterWisps, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.FortifySkills, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.TameRiding, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.NetworkPerformance, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.ShipStats, BepInDependency.DependencyFlags.SoftDependency)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Patch)]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheim.epiclootprogressionfix";
        public const string Version = "1.24.1";
        internal static bool StartupBatchCompleted { get; private set; }
        internal static bool Ready { get; private set; }
        private string startupError;
        private OptionalIntegrations integrations;
        // Patched apart from the gameplay patches, so these destructive actions keep leaving favorites alone if those fail.
        private static readonly Type[] ProtectionPatches =
            { typeof(RecheckSacrificeSelection), typeof(PersonalSacrificeList), typeof(RecheckRuneSelection), typeof(PersonalExtractItemList),
                typeof(RecheckDisenchantSelection), typeof(PersonalDisenchantList), typeof(SalvageJunkRecipeCounts),
                typeof(SalvageJunkMaterialScope), typeof(SalvageJunkInventoryCounts), typeof(SalvageJunkInventoryPayment), typeof(RecheckSalvageJunk) };

        private void Awake()
        {
            DisabledFeatures.Initialize(Logger);
            try
            {
                var batch = BeginWorldPerformanceBatch();
                using (batch) Initialize();
                StartupBatchCompleted = batch != null;
            }
            catch (Exception error) { ReportFailure("Startup patch batch failed: " + error.GetBaseException().Message); }
        }

        private IDisposable BeginWorldPerformanceBatch()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null ||
                !Chainloader.PluginInfos.TryGetValue("tenemo.WorldPerformance", out var performance)) return null;
            // Optional integration: older World Performance releases have no scope API. Contract 1 is independent
            // of this plugin's release number; Initialize must not call methods whose patches are still pending.
            try
            {
                var begin = performance.Instance?.GetType().GetMethod("BeginStartupPatchBatch", new[] { typeof(int) });
                return begin == null ? null : (IDisposable)begin.Invoke(null, new object[] { 1 });
            }
            catch (Exception error)
            {
                // Failure to begin leaves Harmony's ordinary patching intact. Failure to finish is different:
                // Awake reports that as a startup failure, because some wrappers may still be incomplete.
                DisabledFeatures.Set("Startup patch batching", error.GetBaseException().Message);
                return null;
            }
        }

        private void Initialize()
        {
            Ready = false;
            PluginIds.Inspect();
            BindConfig();
            var harmony = new Harmony(Id);
            integrations = new OptionalIntegrations(Logger);
            void Run(Type type, Action<IntegrationPatches> initialize) => integrations.Initialize(type, initialize, harmony);
            try { IntegrationCompatibility.Initialize(new Harmony(Id + ".compatibility"), Logger); }
            catch (Exception error) { ReportFailure("Integration handshake failed: " + error.GetBaseException().Message); }
            // OnGUI draws only a GUI.Box, so Unity can skip its layout pass.
            useGUILayout = false;
            // Save compatibility must not depend on the version-specific gameplay hooks succeeding.
            try
            {
                LegacyStarterGear.Initialize(new Harmony(Id + ".legacy-starter"));
                Logger.LogInfo("Legacy Starter Gear migration enabled: saved IDs resolve to Meadows gear.");
            }
            catch (Exception error) { ReportFailure("Legacy Starter Gear migration failed: " + error.GetBaseException().Message); }

            // A pending enchantment migration keeps the dedicated server closed even when the gameplay patches fail to load.
            try { EnchantmentMigration.Initialize(new Harmony(Id + ".enchantment-migration"), Logger); }
            catch (Exception error) { ReportFailure("Enchantment migration gate failed: " + error.GetBaseException().Message); }

            var protection = new Harmony(Id + ".sacrifice");
            try
            {
                FavoriteProtection.Initialize(Logger);
                foreach (var type in ProtectionPatches)
                {
                    RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                    protection.CreateClassProcessor(type).Patch();
                }
            }
            catch (Exception error) { ReportFailure("Sacrifice, Disenchant, rune extraction and Salvage junk protection failed: " + error.GetBaseException().Message); }

            // Keep durable bindings available even if unrelated gameplay patches cannot initialize.
            try { Run(typeof(DyrrBindingStorage), patches => DyrrBindingStorage.Initialize(patches, Logger)); }
            catch (Exception error) { ReportFailure("World binding storage failed: " + error.GetBaseException().Message); }

            // A refused player must learn why even when the gameplay patches cannot initialize.
            try { Run(typeof(DyrrRefusalReason), patches => DyrrRefusalReason.Initialize(patches)); }
            catch (Exception error) { ReportFailure("Dyrr refusal reason failed: " + error.GetBaseException().Message); }

            // Protects DiscordConnector's records independently of the EpicLoot gameplay patches.
            try { Run(typeof(DiscordConnectorRecords), patches => DiscordConnectorRecords.Initialize(patches, Logger)); integrations.Apply(durableOnly: true); }
            catch (Exception error) { ReportFailure("DiscordConnector records flush failed: " + error.GetBaseException().Message); }

            try { PluginIds.TryGet(PluginIds.EpicLoot, out _); }
            catch (InvalidOperationException error) { ReportFailure(error.Message); }
            if (startupError != null) return;

            try
            {
                Run(typeof(StarLevelSystem), patches => StarLevelSystem.Initialize());
                Run(typeof(NetworkPerformanceOwnership), patches => NetworkPerformanceOwnership.Initialize(patches));
                Run(typeof(DamageTextRange), patches => DamageTextRange.Initialize(patches));
                Run(typeof(DiscordBot), patches => DiscordBot.Initialize());
                Run(typeof(EquipmentAndQuickSlots), patches => EquipmentAndQuickSlots.Initialize());
                Run(typeof(ServerCharacterTemplate), patches => ServerCharacterTemplate.Initialize());
                Run(typeof(CraftingHistory), patches => CraftingHistory.Initialize(patches, Logger));
                Run(typeof(TreasureProgression), patches => TreasureProgression.Initialize(patches, Logger));
                Run(typeof(WithheldEquipmentLog), patches => WithheldEquipmentLog.Initialize(patches, Logger));
                Run(typeof(FirstJoinEnrollment), patches => FirstJoinEnrollment.Initialize(patches, Logger));
                Run(typeof(PersistentFavorites), patches => PersistentFavorites.Initialize(patches, Logger));
                Run(typeof(QuickStackEquipmentSlots), patches => QuickStackEquipmentSlots.Initialize(patches, Logger));
                Run(typeof(QuickStackSorting), patches => QuickStackSorting.Initialize(patches, Logger));
                Run(typeof(ChestPullingDefaults), patches => ChestPullingDefaults.Initialize(patches));
                Run(typeof(QuickStackPullingMode), patches => QuickStackPullingMode.Initialize(patches, Logger));
                Run(typeof(QuickStackKey), patches => QuickStackKey.Initialize(patches, Logger));
                Run(typeof(PlantingGridModifier), patches => PlantingGridModifier.Initialize(patches, Logger));
                Run(typeof(PlantingGridSafety), patches => PlantingGridSafety.Initialize(patches));
                Run(typeof(QuickStackSingleItems), patches => QuickStackSingleItems.Initialize(patches));
                Run(typeof(AutoPickupKey), patches => AutoPickupKey.Initialize(patches, Logger));
                Run(typeof(GraveCarryWeight), patches => GraveCarryWeight.Initialize(patches, Logger));
                Run(typeof(BulkPickup), patches => BulkPickup.Initialize(patches));
                Run(typeof(ChestSupplyEligibility), patches => ChestSupplyEligibility.Initialize(patches));
                Run(typeof(BulkProcessing), patches => BulkProcessing.Initialize(patches));
                Run(typeof(BulkFermenting), patches => BulkFermenting.Initialize(patches));
                Run(typeof(StationInputPreviews), patches => StationInputPreviews.Initialize(patches));
                Run(typeof(StationInputFedo), patches => StationInputFedo.Initialize(patches));
                Run(typeof(StarDropRolls), patches => StarDropRolls.Initialize());
                Run(typeof(StarLootScaling), patches => StarLootScaling.Initialize(patches));
                Run(typeof(CreatureLootBalance), patches => CreatureLootBalance.Initialize(patches));
                Run(typeof(EpicLootStarRewards), patches => EpicLootStarRewards.Initialize(patches));
                Run(typeof(BossLoot), patches => BossLoot.Initialize(patches));
                Run(typeof(BountyLoot), patches => BountyLoot.Initialize(patches, Logger));
                Run(typeof(BossRewardStacks), patches => BossRewardStacks.Initialize(patches));
                Run(typeof(ServingTrayStacks), patches => ServingTrayStacks.Initialize(patches));
                Run(typeof(RuneStacks), patches => RuneStacks.Initialize(patches));
                Run(typeof(PotionBuffRefresh), patches => PotionBuffRefresh.Initialize(patches));
                Run(typeof(ChestRequestLifetime), patches => ChestRequestLifetime.Initialize(patches));
                Run(typeof(ChestRequestRoom), patches => ChestRequestRoom.Initialize(patches));
                Run(typeof(StackLabels), patches => StackLabels.Initialize(patches));
                Run(typeof(PlantHoverText), patches => PlantHoverText.Initialize());
                Run(typeof(BowVolleySound), patches => BowVolleySound.Initialize(this, Logger));
                Run(typeof(TotalArmor), patches => TotalArmor.Initialize(patches));
                Run(typeof(ShieldBalance), patches => ShieldBalance.Initialize());
                Run(typeof(AffixDisplayValues), patches => AffixDisplayValues.Initialize());
                Run(typeof(EffectTooltips), patches => EffectTooltips.Initialize());
                Run(typeof(DungeonInteriors), patches => DungeonInteriors.Initialize(patches, Logger, this));
                Run(typeof(DungeonOccupancy), patches => DungeonOccupancy.Initialize(patches));
                Run(typeof(NorthGraveChests), patches => NorthGraveChests.Initialize(Logger, this));
                Run(typeof(SoulEaterCap), patches => SoulEaterCap.Initialize(patches));
                Run(typeof(SpawnerDamageProtection), patches => SpawnerDamageProtection.Initialize(patches));
                Run(typeof(CreatureAreaDamage), patches => CreatureAreaDamage.Initialize(patches));
                Run(typeof(BountyTargetStrength), patches => BountyTargetStrength.Initialize(patches));
                Run(typeof(FreshCreatureSpawns), patches => FreshCreatureSpawns.Initialize(patches));
                Run(typeof(NightSpawnCleanup), patches => NightSpawnCleanup.Initialize(patches, Logger));
                Run(typeof(SummonedBats), patches => SummonedBats.Initialize(patches));
                Run(typeof(CreatureSpawnPlacement), patches => CreatureSpawnPlacement.Initialize(patches, Logger));
                Run(typeof(StarLevelModifierIcons), patches => StarLevelModifierIcons.Initialize(Logger));
                Run(typeof(SealColors), patches => SealColors.Initialize(patches));
                Run(typeof(MooseAntlers), patches => MooseAntlers.Initialize(patches));
                Run(typeof(BountyPlacement), patches => BountyPlacement.Initialize(patches, Logger));
                Run(typeof(CreatureSizeCompatibility), patches => CreatureSizeCompatibility.Initialize(patches));
                Run(typeof(StarLevelWorldBalance), patches => StarLevelWorldBalance.Initialize(patches));
                Run(typeof(NemesisMinibosses), patches => NemesisMinibosses.Initialize(patches));
                Run(typeof(NemesisScore), patches => NemesisScore.Initialize(patches, Logger));
                Run(typeof(SummonHealth), patches => SummonHealth.Initialize(patches));
                Run(typeof(NemesisOverheadHealthBars), patches => NemesisOverheadHealthBars.Initialize(patches));
                Run(typeof(NemesisHuntingPacks), patches => NemesisHuntingPacks.Initialize(patches, Logger));
                Run(typeof(NemesisFactions), patches => NemesisFactions.Initialize(patches));
                Run(typeof(LateBiomePressure), patches => LateBiomePressure.Initialize(patches));
                Run(typeof(BossStars), patches => BossStars.Initialize(patches));
                Run(typeof(StarLevelAuthority), patches => StarLevelAuthority.Initialize(patches));
                Run(typeof(SharedWeather), patches => SharedWeather.Initialize(patches));
                Run(typeof(AshlandsSeaLight), patches => AshlandsSeaLight.Initialize(patches));
                Run(typeof(ModerWindShift), patches => ModerWindShift.Initialize(patches));
                Run(typeof(TableUpgradeSettlement), patches => TableUpgradeSettlement.Initialize(patches));
                Run(typeof(PheromoneLevels), patches => PheromoneLevels.Initialize(patches));
                Run(typeof(DeathMessageFormatting), patches => DeathMessageFormatting.Initialize(patches, Logger));
                Run(typeof(EnglishItemNames), patches => EnglishItemNames.Initialize(patches));
                Run(typeof(DeathFeedWithoutEmbed), patches => DeathFeedWithoutEmbed.Initialize(patches));
                Run(typeof(DiscordSystemMessages), patches => DiscordSystemMessages.Initialize(patches));
                Run(typeof(DiscordOnlyNotifications), patches => DiscordOnlyNotifications.Initialize(patches));
                Run(typeof(DiscordMentions), patches => DiscordMentions.Initialize(patches));
                Run(typeof(DiscordStartupHelp), patches => DiscordStartupHelp.Initialize(patches));
                Run(typeof(DiscordGatewayMessages), patches => DiscordGatewayMessages.Initialize(patches, Logger));
                Run(typeof(DiscordPlayerStatus), patches => DiscordPlayerStatus.Initialize(patches, Logger));
                Run(typeof(ServerWideChat), patches => ServerWideChat.Initialize(patches));
                Run(typeof(BlockAfterArmor), patches => BlockAfterArmor.Initialize(patches));
                Run(typeof(CreatureStrengthBlocks), patches => CreatureStrengthBlocks.Initialize(patches));
                Run(typeof(DamageTakenEffects), patches => DamageTakenEffects.Initialize(patches));
                Run(typeof(EquipmentTiming), patches => EquipmentTiming.Initialize(patches));
                Run(typeof(CookingProgress), patches => CookingProgress.Initialize(patches));
                Run(typeof(CreatureMeleeReach), patches => CreatureMeleeReach.Initialize(patches));
                Run(typeof(KrigenCharge), patches => KrigenCharge.Initialize(patches));
                Run(typeof(CreatureReachability), patches => CreatureReachability.Initialize(patches));
                Run(typeof(TameRiding), patches => TameRiding.Initialize(patches, Config));
                Run(typeof(SummonOwnership), patches => SummonOwnership.Initialize(patches));
                Run(typeof(SummonNames), patches => SummonNames.Initialize(patches));
                Run(typeof(HexenCollateralDamage), patches => HexenCollateralDamage.Initialize(patches));
                Run(typeof(SummonedFollowers), patches => SummonedFollowers.Initialize(patches));
                Run(typeof(SleepEnemyPause), patches => SleepEnemyPause.Initialize(patches, Logger, this));
                Run(typeof(SummonDismissal), patches => SummonDismissal.Initialize(patches));
                Run(typeof(SummonDungeonTravel), patches => SummonDungeonTravel.Initialize(patches));
                Run(typeof(ProtectionCasterExperience), patches => ProtectionCasterExperience.Initialize(patches));
                Run(typeof(ProtectionBubble), patches => ProtectionBubble.Initialize(patches));
                Run(typeof(CorpseRotKillCredit), patches => CorpseRotKillCredit.Initialize(patches));
                Run(typeof(RangedHitConfirmation), patches => RangedHitConfirmation.Initialize(patches, Logger));
                Run(typeof(MeteorFlight), patches => MeteorFlight.Initialize(patches));
                Run(typeof(PerTargetSkillXp), patches => PerTargetSkillXp.Initialize(patches));
                Run(typeof(EpicLootSkillLevels), patches => EpicLootSkillLevels.Initialize(patches));
                Run(typeof(ResourcePools), patches => ResourcePools.Initialize(patches));
                Run(typeof(InspirationAwards), patches => InspirationAwards.Initialize(patches));
                Run(typeof(WeaponMistRange), patches => WeaponMistRange.Initialize(patches));
                Run(typeof(FedoHudPosition), patches => FedoHudPosition.Initialize(patches));
                Run(typeof(FedoHudSkills), patches => FedoHudSkills.Initialize(Logger));
                Run(typeof(FedoHudPreferences), patches => FedoHudPreferences.Initialize(Logger));
                Run(typeof(FedoHudDeaths), patches => FedoHudDeaths.Initialize(patches));
                Run(typeof(ShipStatsHelm), patches => ShipStatsHelm.Initialize(patches));
                Run(typeof(BetterUiProgressLog), patches => BetterUiProgressLog.Initialize(patches));
                Run(typeof(CookingTooltips), patches => CookingTooltips.Initialize(patches));
                Run(typeof(BetterUiCharacterLayout), patches => BetterUiCharacterLayout.Initialize(patches));
                Run(typeof(FedoHudCooking), patches => FedoHudCooking.Initialize(patches));
                Run(typeof(ArmorDisplay), patches => ArmorDisplay.Initialize(patches));
                Run(typeof(SkillNotifications), patches => SkillNotifications.Initialize(patches, Logger));
                Run(typeof(CharacterCheckpoints), patches => CharacterCheckpoints.Initialize(patches, Logger, this));
                Run(typeof(EmergencyBackupRestore), patches => EmergencyBackupRestore.Initialize(patches, Logger));
                Run(typeof(CoinIncomeChannel), patches => CoinIncomeChannel.Initialize(Logger));
                Run(typeof(ItemActivityChannel), patches => ItemActivityChannel.Initialize(Logger));
                Run(typeof(KeepLitFires), patches => KeepLitFires.Initialize(patches, Logger));
                Run(typeof(DeferredChestLoot), patches => DeferredChestLoot.Initialize(patches, Logger));
                Run(typeof(ChestSupplySwitch), patches => ChestSupplySwitch.Initialize(patches, Logger));
                Run(typeof(AttachedCartMaterials), patches => AttachedCartMaterials.Initialize(patches, Logger));
                Run(typeof(BuildMaterialPull), patches => BuildMaterialPull.Initialize(patches, Logger));
                Run(typeof(SailingShipHealth), patches => SailingShipHealth.Initialize());
                Run(typeof(HelmSimulation), patches => HelmSimulation.Initialize(patches, Logger));
                Run(typeof(PiecePlacement), patches => PiecePlacement.Initialize(patches));
                Run(typeof(FestivalItems), patches => FestivalItems.Initialize(patches));
                Run(typeof(SailingDeathPenalty), patches => SailingDeathPenalty.Initialize(patches));
                Run(typeof(StoreAndCraftInventory), patches => StoreAndCraftInventory.Initialize(patches));
                Run(typeof(ComfortRoomPanel), patches => ComfortRoomPanel.Initialize(patches, Logger));
                Run(typeof(ComfortChipGain), patches => ComfortChipGain.Initialize(patches, Logger));
                Run(typeof(ComfortBuildActions), patches => ComfortBuildActions.Initialize(patches));
                Run(typeof(StationCapacity), patches => StationCapacity.Initialize(patches));
                Run(typeof(ChestHotbarTransfer), patches => ChestHotbarTransfer.Initialize(patches));
                Run(typeof(QuickCartKey), patches => QuickCartKey.Initialize(patches, Logger));
                Run(typeof(AmmoCountBadge), patches => AmmoCountBadge.Initialize(patches, Logger));
                Run(typeof(ModSettingsCaption), patches => ModSettingsCaption.Initialize(patches));
                Run(typeof(CraftingBonus), patches => CraftingBonus.Initialize(patches, Logger));
                Run(typeof(CraftingAmountReset), patches => CraftingAmountReset.Initialize(patches, Logger));
                Run(typeof(CraftingMaxAmount), patches => CraftingMaxAmount.Initialize(patches, Logger));
                Run(typeof(CraftingTutorialOverlay), patches => CraftingTutorialOverlay.Initialize(patches, Logger));
                Run(typeof(CraftingFavoritesIndicator), patches => CraftingFavoritesIndicator.Initialize(patches, Logger));
                Run(typeof(CraftedAnvil), patches => CraftedAnvil.Initialize(patches));
                Run(typeof(CraftingUncraftedFilter), patches => CraftingUncraftedFilter.Initialize(patches, Logger));
                Run(typeof(EnchantingTableFeatures), patches => EnchantingTableFeatures.Initialize(Logger));
                Run(typeof(RecallPriority), patches => RecallPriority.Initialize(patches));
                Run(typeof(RecallRecovery), patches => RecallRecovery.Initialize(patches));
                Run(typeof(AchievementAnnouncements), patches => AchievementAnnouncements.Initialize(patches, Logger));
                Run(typeof(DeathAttribution), patches => DeathAttribution.Initialize(patches));
                Run(typeof(DeathLeaderboard), patches => DeathLeaderboard.Initialize(patches, Logger));
                Run(typeof(CoilingVenomImmunity), patches => CoilingVenomImmunity.Initialize(patches));
                Run(typeof(SetEffectFriendlyFire), patches => SetEffectFriendlyFire.Initialize(patches));
                Run(typeof(HexenFormControls), patches => HexenFormControls.Initialize(patches));
                Run(typeof(VanishHunters), patches => VanishHunters.Initialize(patches));
                Run(typeof(HelsHarvestRaises), patches => HelsHarvestRaises.Initialize(patches));
                Run(typeof(SpellbladeMagicSwords), patches => SpellbladeMagicSwords.Initialize(patches));
                Run(typeof(QuarryMarkInFlight), patches => QuarryMarkInFlight.Initialize(patches));
                foreach (var type in typeof(Plugin).Assembly.GetTypes())
                {
                    if (ProtectionPatches.Contains(type) || type.ContainsGenericParameters || type.IsDefined(typeof(CompilerGeneratedAttribute), false))
                        continue;
                    // Resolve every class's reflected game and mod members now, including those of classes patched in
                    // Initialize, so a changed member fails here instead of at its first use in play.
                    integrations.Collect(type, harmony);
                }
                InvalidTemperRecipes.Initialize();
                integrations.Apply();
                integrations.Finish();
                Ready = true;
                Logger.LogInfo($"Progression fixes loaded for EpicLoot {PluginIds.EpicLootVersion}.");
            }
            catch (Exception error)
            {
                try { integrations.Rollback(); }
                catch (Exception rollback) { ReportFailure("Integration rollback failed: " + rollback.GetBaseException().Message); }
                ReportFailure("Gameplay patches failed: " + error.GetBaseException().Message);
                Logger.LogError(error);
                harmony.UnpatchSelf();
            }
        }

        private void Update()
        {
            if (Ready)
            {
                SimulationPresets.InitializePreference();
                DeathLeaderboard.Tick();
                CharacterCheckpoints.Tick();
                CoinIncomeChannel.Tick();
                ItemActivityChannel.Tick();
                DungeonOccupancy.Tick();
                SummonDungeonTravel.Tick();
                DungeonInteriors.Tick();
                RecallRecovery.Tick();
                RangedHitConfirmation.Cleanup();
                SpawnProtectionOverlay.Tick();
            }
        }

        private void LateUpdate()
        {
            if (Ready) PerTargetSkillXp.FlushFrameAwards();
        }

        private void ReportFailure(string reason)
        {
            Ready = false;
            startupError = startupError == null ? reason : startupError + "\n" + reason;
            Logger.LogError(reason);
        }

        private void OnGUI()
        {
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) return;
            string text;
            if (startupError != null)
                text = "EpicLoot Progression Fix failed to initialize\n" + startupError +
                    "\nCheck the game log and repair the mod installation before playing.";
            else
            {
                // A damaged record affects only the loaded character; the next character load clears this.
                if (CraftingHistory.Problem == null && PersistentFavorites.Problem == null && RecallRecovery.Problem == null) return;
                string problems = string.Join("\n", new[] { CraftingHistory.Problem, PersistentFavorites.Problem, RecallRecovery.Problem }.Where(x => x != null));
                text = "EpicLoot Progression Fix could not read this character's saved data\n" + problems +
                    "\nAsk the server owner to restore the saved character data.";
            }
            var style = new GUIStyle(GUI.skin.box) { wordWrap = true, fontSize = 16, alignment = TextAnchor.MiddleCenter };
            style.normal.textColor = Color.yellow;
            var message = new GUIContent(text);
            float width = Math.Max(260, Screen.width - 40);
            // Grow with the number of reported failures instead of clipping them, within the screen.
            float height = Math.Min(Math.Max(120f, style.CalcHeight(message, width)), Math.Max(120f, Screen.height - 40f));
            GUI.Box(new Rect(20, 20, width, height), message, style);
        }
    }
}
