using BepInEx.Configuration;
using UnityEngine;

namespace EpicLootProgressionFix
{
    public sealed partial class Plugin
    {
        internal static ConfigEntry<bool> DungeonInteriorResets;
        internal static ConfigEntry<int> CharacterCheckpointMinutes;
        internal static ConfigEntry<float> EpicLootStarRewardIncrease;
        internal static ConfigEntry<float> OrdinaryMagicLootShare, BossMagicLootShare, TemplateHighRarityShare;
        internal static ConfigEntry<float> RestedDelayMultiplier;
        internal static ConfigEntry<float> AndvaranautSearchRadius;
        internal static ConfigEntry<float> AndvaranautFacingAngle;
        internal static ConfigEntry<float> AndvaranautMaxRange;
        internal static ConfigEntry<bool> DiscordAchievements;
        internal static ConfigEntry<bool> DailyDeathLeaderboard;
        internal static ConfigEntry<bool> DeathLeaderboardDryRun;
        internal static ConfigEntry<string> DeathReportPlayerTitle, DeathReportCauseTitle, DeathReportOtherPlayers, DeathReportOtherCauses;
        internal static ConfigEntry<string> DeathReportNoDeaths, DeathReportTotalDeaths, DeathReportMissingName, DeathReportUnknownCause, DeathReportTimeZone;
        internal static ConfigEntry<bool> CoinIncomeEnabled;
        internal static ConfigEntry<bool> ItemActivityEnabled;
        internal static ConfigEntry<float> AndvaranautSlowestPing, AndvaranautSearchRadiusPing, AndvaranautNearestPing;
        internal static ConfigEntry<float> ClearWeatherWeight;
        internal static ConfigEntry<float> LovePotionTrollSpawnChance, LovePotionZoneRecheckSeconds, LovePotionStarChanceMultiplier;
        internal static ConfigEntry<int> LovePotionMinimumTrollStars;
        internal static ConfigEntry<float> CreatureSpawnMinimum, CreatureSpawnMaximum, LootBonusRadius;
        internal static ConfigEntry<string> CreatureRarityCeilings, OozerLuckyLootExclusions;
        internal static ConfigEntry<float> HighRarityEquipmentShare, HoardingRewardMultiplier;
        internal static ConfigEntry<float> StaminaRegenDelayReduction, ComfortReach, SoulEaterDamageCap, GjallBaseMass;
        internal static ConfigEntry<float> BatPursuitRange, AttackerHealthBarRange, BountySpacing, SnowLanternLightRadius, CartHealthBonus;
        internal static ConfigEntry<int> BountyExtraEquipmentCopies, RuneStackSize, DeathLeaderboardHour;
        internal static ConfigEntry<float> BuffRefreshWindowPercent, DroppedItemLifetimeHours;
        internal static ConfigEntry<float> ShieldBlockArmorMultiplier, ShieldBlockForceMultiplier, RoundShieldParryIncrease, BucklerParryIncrease, CreatureBlockGrowth, StaggerHealthShare, BaseParryWindow;
        internal static ConfigEntry<bool> SmeltingRequiresBosses;
        internal static ConfigEntry<float> ShipZoomDistance, LandZoomMultiplier, DamageTextDistance, OwnDamageTextDistance, OffsetAttackVolume;
        internal static ConfigEntry<KeyCode> SpawnProtectionToggleKey;
        internal static ConfigEntry<Color> SpawnProtectionGroundColor, SpawnProtectionStripeColor, SpawnProtectionStripeShadowColor;
        internal static ConfigEntry<float> HarvestRadius;
        internal static ConfigEntry<float> FloatingIceImpactMultiplier;
        internal static ConfigEntry<bool> YuleAllYear, HalloweenAllYear, MidsummerAllYear;
        internal static ConfigEntry<string> DefaultChestPullingMode;
        internal static ConfigEntry<float> LateBiomeSightRange, LateBiomeAlertRange, LateBiomeTargetMemory, LateBiomeAttackTimeout, LateBiomeFastSpeed, LateBiomeHeavySpeed, CharredBowRange;
        internal static ConfigEntry<float> AshlandsBuildingWear;

        // Values every game must apply alike follow the server; Jotunn synchronizes only entries tagged with its own
        // attributes type. Display preferences and server-only values do not sync.
        internal static ConfigDescription Synced(string text, AcceptableValueBase range = null) =>
            new ConfigDescription(text + " [Synced with Server]", range, new ConfigurationManagerAttributes { IsAdminOnly = true });

        private void BindConfig()
        {
            DefaultChestPullingMode = Config.Bind("Storage", "Default chest pulling mode", "Inherit",
                Synced("Initial nearby-container mode for characters without a saved choice. Inherit uses AzuCraftyBoxes' default.",
                    new AcceptableValueList<string>("Inherit", "Off", "All", "Only mine")));
            FloatingIceImpactMultiplier = Config.Bind("Ships", "Floating ice impact damage multiplier", 1f,
                Synced("Ship collision damage multiplier when hitting drifting ocean ice, floating shore shards or ice shelf floes.", new AcceptableValueRange<float>(0f, 100f)));
            CoinIncomeEnabled = Config.Bind("Private coin income", "Enabled", true,
                Synced("Privately record currency transactions on the dedicated server. Requires ServerCharacters."));
            ItemActivityEnabled = Config.Bind("Private item activity", "Enabled", true,
                Synced("Privately record item pickups, transfers, crafting, purchases and enchanting on the dedicated server. Requires ServerCharacters."));
            DungeonInteriorResets = Config.Bind("Dungeons", "Interior-only resets", true,
                Synced("Reset configured dungeon interiors only. Timers start on first entry; occupied interiors wait until players leave."));
            CharacterCheckpointMinutes = Config.Bind("Characters", "Checkpoint interval minutes", 0,
                new ConfigDescription("Dedicated server: minutes between periodic character saves. 0 disables periodic saves.",
                    new AcceptableValueRange<int>(0, 60)));
            EpicLootStarRewardIncrease = Config.Bind("Creature loot", "Reward increase per star above five", 0.1f,
                Synced("Ordinary creatures keep five-star base loot above five stars. Each further star adds this share of magic-loot rolls and Legendary/Mythic/Ancient rarity weight. 0.1 gives a 50% bonus at ten stars. 0 uses the usual uncapped loot scaling.", new AcceptableValueRange<float>(0f, 1f)));
            OrdinaryMagicLootShare = Config.Bind("Creature loot", "Ordinary magic loot share", 1f,
                Synced("Share of ordinary creatures' successful magic-loot rolls to keep.", new AcceptableValueRange<float>(0f, 1f)));
            BossMagicLootShare = Config.Bind("Creature loot", "Boss magic loot share", 1f,
                Synced("Share of successful boss equipment rolls to keep, with at least one roll.", new AcceptableValueRange<float>(0f, 1f)));
            TemplateHighRarityShare = Config.Bind("Creature loot", "Template high-rarity share", 1f,
                Synced("Share of ordinary creatures' Legendary, Mythic and Ancient chances to keep. The rest moves equally to Rare and Epic.", new AcceptableValueRange<float>(0f, 1f)));
            DailyDeathLeaderboard = Config.Bind("Death leaderboard", "Enabled", true,
                "Dedicated server only: report the preceding 24 hours of deaths at the configured hour and time zone through DiscordConnector's primary webhook. Restart after changing.");
            DeathLeaderboardDryRun = Config.Bind("Death leaderboard", "Dry run", false,
                "Dedicated server only: preview reports in the log. Restart after changing.");
            var reportDefaults = new DeathReport.Text();
            ConfigEntry<string> ReportText(string name, string value) => Config.Bind("Death leaderboard", name, value,
                "Dedicated server only: report wording. Blank uses the English default; long text is shortened. Restart after changing.");
            DeathReportPlayerTitle = ReportText("Player report title", reportDefaults.PlayerTitle);
            DeathReportCauseTitle = ReportText("Cause report title", reportDefaults.CauseTitle);
            DeathReportOtherPlayers = ReportText("Other players label", reportDefaults.OtherPlayers);
            DeathReportOtherCauses = ReportText("Other causes label", reportDefaults.OtherCauses);
            DeathReportNoDeaths = ReportText("No deaths message", reportDefaults.NoDeaths);
            DeathReportTotalDeaths = ReportText("Total deaths label", reportDefaults.TotalDeaths);
            DeathReportMissingName = ReportText("Missing name", reportDefaults.MissingName);
            DeathReportUnknownCause = ReportText("Unknown cause", reportDefaults.UnknownCause);
            DeathReportTimeZone = Config.Bind("Death leaderboard", "Report time zone", "UTC",
                "Dedicated server only: time zone identifier, such as UTC or Europe/Warsaw. Default: UTC. Restart after changing.");
            DiscordAchievements = Config.Bind("Achievements", "Discord announcements", true,
                Synced("Announce new achievements through DiscordBot. Excludes startup and retroactive unlocks."));
            RestedDelayMultiplier = Config.Bind("Resting", "Rested delay multiplier", 1f,
                Synced("Multiplier for the wait before Rested activates. 0.5 halves the wait.", new AcceptableValueRange<float>(0f, 1f)));
            AndvaranautSearchRadius = Config.Bind("Andvaranaut", "Search radius", 1000f,
                Synced("Within this distance in meters, Andvaranaut pings without facing the target. 0 requires facing it at every distance.", new AcceptableValueRange<float>(0f, 10000f)));
            AndvaranautFacingAngle = Config.Bind("Andvaranaut", "Facing angle", 30f,
                Synced("Outside the search radius, ping when facing within this many degrees of the target. 0 disables facing pings.", new AcceptableValueRange<float>(0f, 90f)));
            AndvaranautMaxRange = Config.Bind("Andvaranaut", "Max range", 0f,
                Synced("Maximum tracking distance in meters to an unopened treasure chest or accepted bounty. 0 means unlimited.", new AcceptableValueRange<float>(0f, 10000f)));
            CreatureSpawnMinimum = Config.Bind("Creatures", "Ordinary spawn minimum", 80f,
                Synced("Minimum distance in meters to an ordinary biome spawn's group center.", new AcceptableValueRange<float>(0f, 1000f)));
            CreatureSpawnMaximum = Config.Bind("Creatures", "Ordinary spawn maximum", 120f,
                Synced("Maximum distance in meters to an ordinary biome spawn's group center. Set at least the minimum.", new AcceptableValueRange<float>(0f, 1000f)));
            DroppedItemLifetimeHours = Config.Bind("Dropped items", "Lifetime hours", 3f,
                Synced("Hours of world time before unprotected dropped items disappear.", new AcceptableValueRange<float>(0.1f, 1000f)));
            LootBonusRadius = Config.Bind("Creature loot", "Equipment bonus radius", 200f,
                Synced("Riches, Luck and Lucky Loot count players within this many meters of the drop.", new AcceptableValueRange<float>(0f, 1000f)));
            OozerLuckyLootExclusions = Config.Bind("Creature loot", "Oozer Lucky Loot exclusions", "",
                Synced("Comma-separated character names. Lucky Loot does not apply to an oozer dying within the equipment bonus radius of any listed character."));
            AndvaranautSlowestPing = Config.Bind("Andvaranaut", "Slowest ping interval", 8f,
                Synced("Seconds between pings at maximum range, or beyond the search radius when range is unlimited.",
                    new AcceptableValueRange<float>(0.1f, 60f)));
            AndvaranautSearchRadiusPing = Config.Bind("Andvaranaut", "Search radius ping interval", 1f,
                Synced("Seconds between pings at the search radius.",
                    new AcceptableValueRange<float>(0.1f, 60f)));
            AndvaranautNearestPing = Config.Bind("Andvaranaut", "Nearest ping interval", 0.5f,
                Synced("Seconds between pings at the target.", new AcceptableValueRange<float>(0.1f, 60f)));
            ClearWeatherWeight = Config.Bind("Weather", "Clear weight", 20f,
                Synced("Clear-weather weight in Meadows, Black Forest, Plains and Ocean. 20 gives an 80% clear-weather chance.",
                    new AcceptableValueRange<float>(0f, 1000f)));
            LovePotionTrollSpawnChance = Config.Bind("Love Potion", "Troll spawn chance", 35f,
                Synced("While a player with the Love Potion is in a zone, its troll spawns succeed with at least this percent chance.",
                    new AcceptableValueRange<float>(0f, 100f)));
            LovePotionZoneRecheckSeconds = Config.Bind("Love Potion", "Zone recheck seconds", 2000f,
                Synced("While the potion lasts, a zone rolls its troll spawns again after at most this many world seconds.",
                    new AcceptableValueRange<float>(1f, 100000f)));
            LovePotionMinimumTrollStars = Config.Bind("Love Potion", "Minimum troll stars", 2,
                Synced("Trolls spawned for the potion get at least this many stars, within the creature's maximum.", new AcceptableValueRange<int>(0, 10)));
            LovePotionStarChanceMultiplier = Config.Bind("Love Potion", "Further star chance multiplier", 1.5f,
                Synced("Multiplies StarLevelSystem's chance of each further star for trolls spawned for the potion.", new AcceptableValueRange<float>(0f, 10f)));
            CreatureRarityCeilings = Config.Bind("Creature loot", "Rarity ceilings",
                "Moose:Mythic, Elaking:Legendary, ElakingLantern:Legendary, Skeleton_DeepNorth:Legendary, Greydwarf_Frozen:Legendary, Greydwarf_Shaman_Frozen:Legendary, Volture:Legendary, BlobLava:Legendary",
                Synced("Highest equipment rarity each listed creature can drop. Comma-separated Prefab:Rarity pairs using Magic, Rare, Epic, Legendary, Mythic or Ancient. Bosses are exempt.",
                    null));
            HighRarityEquipmentShare = Config.Bind("Creature loot", "High-rarity equipment share", 0.5f,
                Synced("Share of creature and boss equipment's Legendary, Mythic and Ancient chances to keep. The rest moves to Rare.",
                    new AcceptableValueRange<float>(0f, 1f)));
            HoardingRewardMultiplier = Config.Bind("Creature loot", "Hoarding reward multiplier", 2f,
                Synced("Creatures with StarLevelSystem's Hoarding modifier multiply their EpicLoot rolls and Legendary, Mythic and Ancient rarity weight by this. 1 gives Hoarding no EpicLoot bonus.",
                    new AcceptableValueRange<float>(1f, 10f)));
            StaminaRegenDelayReduction = Config.Bind("Stamina", "Regen delay reduction", 0.8f,
                Synced("Share of the wait before stamina refills that EpicLoot stamina regeneration removes at +100%, in proportion below: 0.8 turns the game's 1 second into 0.6 at +50% and 0.2 at +100%.",
                    new AcceptableValueRange<float>(0f, 1f)));
            ComfortReach = Config.Bind("Resting", "Comfort radius", 20f,
                Synced("Comfort furniture counts within this distance in meters, including vertically.",
                    new AcceptableValueRange<float>(1f, 100f)));
            SoulEaterDamageCap = Config.Bind("Creatures", "SoulEater damage cap", 0.5f,
                Synced("Maximum damage bonus from SoulEater. 0.5 allows +50% damage for an ordinary creature.",
                    new AcceptableValueRange<float>(0f, 100f)));
            GjallBaseMass = Config.Bind("Creatures", "Gjall mass", 100f,
                Synced("Gjall's base physics mass, which sets how far hits push it. Applies when a Gjall spawns or loads.", new AcceptableValueRange<float>(1f, 10000f)));
            BatPursuitRange = Config.Bind("Summoned bats", "Pursuit range", 60f,
                Synced("Summoned bats take and keep targets only within this many meters of their summoner, and a creature a bat hits retaliates against the summoner only within it.",
                    new AcceptableValueRange<float>(5f, 500f)));
            AttackerHealthBarRange = Config.Bind("Health bars", "Attacker range", 15f,
                Synced("An alerted hostile creature targeting you within this many meters keeps its health bar through walls, rocks and terrain.",
                    new AcceptableValueRange<float>(0f, 100f)));
            BountySpacing = Config.Bind("Bounties", "Spacing", 300f,
                Synced("A new bounty is placed at least this many meters from every open bounty and unopened treasure chest, or as far from them as possible when no suitable spot is that far.",
                    new AcceptableValueRange<float>(0f, 5000f)));
            BountyExtraEquipmentCopies = Config.Bind("Bounties", "Extra enchanted copies", 1,
                Synced("Extra copies of each enchanted equipment drop from a bounty's main target, each with its own enchantments.",
                    new AcceptableValueRange<int>(0, 5)));
            SnowLanternLightRadius = Config.Bind("Lights", "Snow lantern radius", 5f,
                Synced("Light radius of snow lanterns in meters, applied as lanterns are built or loaded.", new AcceptableValueRange<float>(0.5f, 50f)));
            CartHealthBonus = Config.Bind("Carts", "Crafting health bonus", 4f,
                Synced("A cart gains this multiple of its base health at its builder's Crafting 100, in proportion below: 4 gives +4% per level and +400% at 100. Every crafted cart applies it when it loads, so a change also rescales existing carts.",
                    new AcceptableValueRange<float>(0f, 20f)));
            RuneStackSize = Config.Bind("Runes", "Etched rune stack size", 10,
                Synced("Maximum stack of etched runes with matching effects and power. Lowering it splits larger stacks when loaded or handled.",
                    new AcceptableValueRange<int>(1, 1000)));
            BuffRefreshWindowPercent = Config.Bind("Potions", "Buff refresh window percent", 10f,
                Synced("A mead or other consumable whose effect lasts a set time and restores no health, stamina or eitr can be taken again once at most this percent of the active effect's lifetime is left. The new one replaces it with a fresh effect, and the leftover time is lost. 0 keeps the game's rule of waiting for the effect to end.",
                    new AcceptableValueRange<float>(0f, 100f)));
            ShieldBlockArmorMultiplier = Config.Bind("Shields", "Block armor multiplier", 1.25f,
                Synced("Block armor multiplier for craftable shields, including gains per quality.",
                    new AcceptableValueRange<float>(0.1f, 10f)));
            ShieldBlockForceMultiplier = Config.Bind("Shields", "Block force multiplier", 1.25f,
                Synced("Block knockback multiplier for craftable shields, including gains per quality.",
                    new AcceptableValueRange<float>(0.1f, 10f)));
            RoundShieldParryIncrease = Config.Bind("Shields", "Round shield parry increase", 0.5f,
                Synced("Added to craftable round shields' parry multiplier. 0.5 raises 1.5x to 2x.",
                    new AcceptableValueRange<float>(0f, 10f)));
            BucklerParryIncrease = Config.Bind("Shields", "Buckler parry increase", 1f,
                Synced("Added to the parry multiplier of every craftable buckler: 1 turns the game's 2.5x into 3.5x.",
                    new AcceptableValueRange<float>(0f, 10f)));
            CreatureBlockGrowth = Config.Bind("Shields", "Block growth against strong creatures", 0.5f,
                Synced("Blocks and parries against a creature grow by its extra damage from stars, modifiers and world difficulty raised to this power. 0.5 grows them by its square root; 0 keeps the shield's own block armor. A block that breaks your guard still blocks its amount.",
                    new AcceptableValueRange<float>(0f, 1f)));
            StaggerHealthShare = Config.Bind("Players", "Stagger share of max health", 0.6f,
                Synced("Stagger damage needed as a share of maximum health. A block or parry that reaches it fails.",
                    new AcceptableValueRange<float>(0.1f, 10f)));
            BaseParryWindow = Config.Bind("Players", "Base parry window seconds", 0.375f,
                Synced("Base seconds after raising a shield or parrying weapon during which a block counts as a parry.",
                    new AcceptableValueRange<float>(0.05f, 2f)));
            SmeltingRequiresBosses = Config.Bind("Smelting", "Require boss defeats", true,
                Synced("Smelting flametal requires defeating Yagluth; smelting bloodgold requires defeating the Queen."));
            DeathLeaderboardHour = Config.Bind("Death leaderboard", "Report hour", 6,
                new ConfigDescription("Dedicated server only: daily report hour in Report time zone. Restart after changing.",
                    new AcceptableValueRange<int>(0, 23)));
            ShipZoomDistance = Config.Bind("Camera", "Ship zoom distance", 24f,
                new ConfigDescription("Farthest camera zoom in meters for the helmsman and passengers on a ship.", new AcceptableValueRange<float>(1f, 100f)));
            LandZoomMultiplier = Config.Bind("Camera", "Land zoom multiplier", 1.5f,
                new ConfigDescription("Maximum camera zoom multiplier while off ships.", new AcceptableValueRange<float>(1f, 5f)));
            DamageTextDistance = Config.Bind("Damage text", "Maximum distance", 100f,
                new ConfigDescription("Maximum camera distance in meters for floating combat text from other sources.", new AcceptableValueRange<float>(1f, 500f)));
            OwnDamageTextDistance = Config.Bind("Damage text", "Own damage maximum distance", 200f,
                new ConfigDescription("Maximum camera distance in meters for damage you deal, including poison, fire and spirit ticks.", new AcceptableValueRange<float>(1f, 500f)));
            OffsetAttackVolume = Config.Bind("Sounds", "OffSet Attack volume", 0.25f,
                new ConfigDescription("Volume of EpicLoot's OffSet Attack hit sound, under the game's sound effects slider.", new AcceptableValueRange<float>(0f, 1f)));
            SpawnProtectionToggleKey = Config.Bind("Spawn protection view", "Toggle key", KeyCode.G,
                "Pressed with Ctrl while holding the hammer, toggles the spawn protection view.");
            SpawnProtectionGroundColor = Config.Bind("Spawn protection view", "Ground color", (Color)new Color32(191, 51, 5, 82),
                "Color and transparency of protected ground.");
            SpawnProtectionStripeColor = Config.Bind("Spawn protection view", "Stripe color", new Color(1f, 0.22f, 0.02f, 0.62f),
                "Color and transparency of the border's bright stripes.");
            SpawnProtectionStripeShadowColor = Config.Bind("Spawn protection view", "Stripe shadow color", new Color(0.30f, 0.05f, 0.005f, 0.28f),
                "Color and transparency of the border's dark stripes.");
            const string LateBiome = "Late-biome pressure";
            LateBiomeSightRange = Config.Bind(LateBiome, "Minimum sight range", 0f,
                Synced("Minimum sight distance in meters for Charred infantry and casters, northern skeletons, frozen greydwarves, Fryslings and Jotun warriors spawned in Ashlands or Deep North. Excludes tames and bosses. 0 uses the creature's usual range.", new AcceptableValueRange<float>(0f, 200f)));
            LateBiomeAlertRange = Config.Bind(LateBiome, "Minimum alert range", 0f,
                Synced("Minimum distance in meters at which these creatures become alerted to a visible player. 0 uses the creature's usual range.", new AcceptableValueRange<float>(0f, 200f)));
            LateBiomeTargetMemory = Config.Bind(LateBiome, "Target memory seconds", 30f,
                Synced("Seconds these creatures remember a target they can neither see nor hear.", new AcceptableValueRange<float>(1f, 3600f)));
            LateBiomeAttackTimeout = Config.Bind(LateBiome, "Attack timeout seconds", 60f,
                Synced("Seconds these creatures pursue without attacking before giving up. Creatures hunting players are exempt.", new AcceptableValueRange<float>(1f, 3600f)));
            LateBiomeFastSpeed = Config.Bind(LateBiome, "Fast pursuit speed multiplier", 1f,
                Synced("Walking and running speed multiplier for Charred twitchers, frozen greydwarves and Fryslings.", new AcceptableValueRange<float>(0.5f, 3f)));
            LateBiomeHeavySpeed = Config.Bind(LateBiome, "Heavy infantry speed multiplier", 1f,
                Synced("Walking and running speed multiplier for Charred warriors, northern skeletons and Jotun warriors.", new AcceptableValueRange<float>(0.5f, 3f)));
            CharredBowRange = Config.Bind(LateBiome, "Charred bow range", 0f,
                Synced("Minimum range in meters for Charred archers' ordinary bow attacks. 0 uses their usual range.", new AcceptableValueRange<float>(0f, 100f)));
            AshlandsBuildingWear = Config.Bind("Ashlands", "Building wear", 0.5f,
                Synced("Percent of maximum health that Ashlands heat takes about every 6 seconds from uncovered pieces that can catch fire, while a player is nearby. Ships, carts, planted Ashvines and pieces under a roof or inside a shield generator are spared. 0 turns it off.",
                    new AcceptableValueRange<float>(0f, 5f)));
            YuleAllYear = Config.Bind("Festivals", "Yule items all year", false,
                Synced("Yuleklapps, the Yule tree, wreath and garland and mistletoe can be built all year, not only from 1 December to 6 January."));
            HalloweenAllYear = Config.Bind("Festivals", "Halloween items all year", false,
                Synced("The Jack-o-turnip can be built and the pointy hat crafted all year, not only from 1 October to 6 November."));
            MidsummerAllYear = Config.Bind("Festivals", "Midsummer items all year", false,
                Synced("The maypole can be built and the Midsummer crown crafted all year, not only from 1 June to 6 July."));
            HarvestRadius = Config.Bind("Bulk interactions", "Harvest radius", 0f,
                "Radius in meters for PlantEasily harvesting, loose-loot pickup and cooking stations. Zero leaves the current radius unchanged. Pickup-range enchantments increase it.");
        }
    }
}
