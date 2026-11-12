using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal sealed class OptionalIntegrations
    {
        private sealed class Group
        {
            internal string Name, Title;
            internal bool Shared, Failed, Active, Durable;
            internal string[] Plugins;
            internal IntegrationPatches Patches;
            internal readonly HashSet<Type> Snapshots = new HashSet<Type>();
        }
        private readonly Dictionary<string, Group> owners = new Dictionary<string, Group>(StringComparer.Ordinal);
        private readonly List<Group> groups = new List<Group>();
        private readonly List<Group> applicationOrder = new List<Group>();
        private readonly ManualLogSource logger;
        private static OptionalIntegrations current;
        internal static IntegrationPatches Preparing { get; private set; }
        internal static string Warning => current == null ? null : string.Join("\n", current.groups.Where(group => group.Failed).Select(group => group.Title));

        internal OptionalIntegrations(ManualLogSource log)
        {
            current = this; logger = log;
            Add("quick-stack", "Quick Stack sorting, restocking and controls", true, new[] { PluginIds.QuickStack },
                "QuickStackSorting", "QuickStackKey", "QuickStackSingleItems", "ItemActivity+Restocking");
            Add("favorite-storage", "Personal favorites and inventory borders", true, new[] { PluginIds.QuickStack, PluginIds.ItemStacks }, "PersistentFavorites", "SocketFavorites");
            Add("equipment-slots", "Equipment slots, grave recovery and slot shortcuts", true, new[] { PluginIds.EquipmentAndQuickSlots },
                "EquipmentAndQuickSlots", "AutoPickupKey", "GraveCarryWeight", "GraveSlotRecovery", "InventorySlotKeys", "RecallSlots+QuickSlots");
            Add("quick-stack-equipment", "Quick Stack equipment-slot protection", true, new[] { PluginIds.QuickStack, PluginIds.EquipmentAndQuickSlots }, "QuickStackEquipmentSlots");
            Add("chest-storage", "Nearby chest supply, cooking and station capacity", true, new[] { PluginIds.AzuCraftyBoxes },
                "AttachedCartMaterials", "BuildMaterialPull", "StationCapacity", "BulkCooking", "BulkProcessing", "BulkFermenting", "StationInputPreviews", "ChestPullingDefaults", "ChestSupplyEligibility", "HelmSimulation+StorageSupply");
            Add("quick-stack-pulling", "Shared chest pulling modes and exclusions", true, new[] { PluginIds.AzuCraftyBoxes, PluginIds.QuickStack }, "ChestSupplySwitch", "QuickStackPullingMode");
            Add("crafting", "Batch crafting, recipe filters and controls", true, new[] { PluginIds.AaaCrafting },
                "CraftingAmountReset", "CraftingBonus", "CraftingFavoritesIndicator", "CraftingUncraftedFilter", "CraftingTutorialOverlay");
            Add("crafting-supply", "Batch crafting maximum from nearby supply", true, new[] { PluginIds.AaaCrafting, PluginIds.AzuCraftyBoxes }, "CraftingMaxAmount");
            Add("multi-user-storage", "Shared-chest transfers, consumables and labels", true, new[] { PluginIds.MultiUserChest },
                "PotionBuffRefresh", "StackLabels", "ChestRequestLifetime", "ChestRequestRoom", "ItemActivity+ChestResponses", "ItemActivity+ChestRequests", "HelmSimulation+StorageRequests");
            Add("chest-hotbar", "Chest-to-hotbar transfers", true, new[] { PluginIds.MultiUserChest, PluginIds.EquipmentAndQuickSlots }, "ChestHotbarTransfer");
            Add("item-stacks", "Reward, trader, serving tray and mead-base stacks", true, new[] { PluginIds.ItemStacks }, "BossRewardStacks", "ServingTrayStacks", "TraderStacking");
            Add("rune-stacks", "Rune stack limits and matching", true, new[] { PluginIds.ItemStacks, PluginIds.QuickStack }, "RuneStacks");
            Add("server-characters", "Character enrollment, verified saves and private journals", true, new[] { PluginIds.ServerCharacters },
                "CharacterCheckpoints", "FirstJoinEnrollment", "ServerCharacterTemplate", "CoinIncomeChannel", "ItemActivityChannel", "ItemActivity", "TreasureDiscoveries");
            Add("star-level-system", "Creature scaling, boss rewards, Nemesis and dungeon resets", true, new[] { PluginIds.StarLevelSystem },
                "StarLevelSystem", "StarLevelModifierIcons", "StarLevelWorldBalance", "StarLevelAuthority", "BossStars", "BountyTargetStrength",
                "CreatureAreaDamage", "CreatureSizeCompatibility", "DungeonInteriors", "DungeonOccupancy", "EpicLootStarRewards", "FreshCreatureSpawns", "LateBiomePressure",
                "MiningScaledDropPosition", "ModSettingsCaption", "NemesisFactions", "NemesisHuntingPacks", "NemesisMinibosses", "NemesisScore", "NemesisOverheadHealthBars", "PheromoneLevels", "PoisonConversion",
                "SealColors", "MooseAntlers", "SoulEaterCap", "StarLootScaling", "SummonHealth", "ItemActivity+SpreadLoot");
            Add("drop-tables", "Star drop-roll compatibility", true, new[] { PluginIds.DropThat }, "StarDropRolls");
            Add("discord", "Discord chat, death messages, achievements and startup behavior", true, new[] { PluginIds.DiscordBot },
                "DiscordBot", "AchievementAnnouncements", "DeathFeedWithoutEmbed", "DiscordOnlyNotifications", "DiscordMentions", "DiscordPlayerStatus", "DiscordScreenshotHud", "DiscordStartupHelp", "DiscordGatewayMessages");
            Add("discord-file-log", "Discord records durability", false, new[] { PluginIds.DiscordConnector }, "DiscordConnectorRecords");
            Add("discord-records", "Discord death reports", false, new[] { PluginIds.DiscordConnector }, "DeathLeaderboard");
            Add("discord-presentation", "Discord system-message formatting", false, new[] { PluginIds.DiscordBot, PluginIds.DiscordConnector }, "DiscordSystemMessages");
            Add("death-presentation", "Death-message names and star formatting", false, new[] { PluginIds.DiscordBot, PluginIds.StarLevelSystem }, "DeathMessageFormatting");
            Add("plant-harvest", "Planting controls and area harvesting", false, new[] { PluginIds.PlantEasily }, "BulkPickup", "PlantingGridModifier", "PlantingGridSafety");
            Add("plant-hover", "Plant growth hover text", false, new[] { PluginIds.BetterUi, PluginIds.PlantEverything }, "PlantHoverText");
            Add("better-ui", "Armor, cooking and progress display compatibility", false, new[] { PluginIds.BetterUi }, "ArmorDisplay", "BetterUiProgressLog", "CookingTooltips", "BetterUiCharacterLayout");
            Add("skill-notices", "Skill progress notifications", false, new[] { PluginIds.BetterUi, PluginIds.FortifySkills }, "SkillNotifications");
            Add("fedo-hud", "Personal FedoHud preferences and overlays", false, new[] { PluginIds.FedoHud }, "FedoHudDeaths", "FedoHudPosition", "FedoHudSkills", "FedoHudPreferences", "FedoHudCooking", "StationInputFedo");
            Add("comfort", "Comfort room panel, chips and build actions", false, new[] { PluginIds.ComfortMe }, "ComfortBuildActions", "ComfortChipGain", "ComfortRoomPanel");
            Add("ammo-count", "Ammunition counter placement", false, new[] { PluginIds.AmmoCount }, "AmmoCountBadge");
            Add("ship-stats", "Ship Stats panel while steering", false, new[] { PluginIds.ShipStats }, "ShipStatsHelm");
            Add("logout-effects", "Logout status snapshots and Rested comfort", false, new[] { PluginIds.LogoutTweaks }, "DeadLogoutEffects", "LogoutRestedComfort");
            Add("sailing", "Sailing ship health and death penalty", true, new[] { PluginIds.Sailing }, "SailingShipHealth", "SailingDeathPenalty");
            Add("carry-weight", "Effective skills in carry weight", true, new[] { PluginIds.SkilledCarryWeight }, "EpicLootSkillLevels");
            Add("fortify-experience", "Inspiration experience compatibility", true, new[] { PluginIds.FortifySkills }, "InspirationAwards");
            Add("kept-fires", "Kept-lit fire compatibility", true, new[] { PluginIds.NoSmokeStayLit }, "KeepLitFires");
            Add("weapon-mist", "Held-weapon mist clearing", true, new[] { PluginIds.BetterWisps }, "WeaponMistRange");
            Add("quick-cart", "Quick cart controls", true, new[] { PluginIds.QuickCart }, "QuickCartKey");
            Add("dyrr-bindings", "Durable world bindings and refusal reasons", true, new[] { PluginIds.Dyrr }, "DyrrBindingStorage", "DyrrRefusalReason");
            Add("tame-riding", "Tame riding stamina, controls, speeds, clearing and Sit", true, new[] { PluginIds.TameRiding }, "TameRiding");
            Add("server-password", "Password box after a refused saved password", false, new[] { PluginIds.FastLink }, "ServerPasswordReset", "FastLinkMenuReload", "FastLinkNameCasing");
            Add("store-and-craft", "StoreAndCraft inventory supply", true, new[] { PluginIds.StoreAndCraft }, "StoreAndCraftInventory");
            foreach (var group in groups) group.Durable = group.Name == "dyrr-bindings" || group.Name == "discord-file-log";
            foreach (string type in owners.Keys)
                if (typeof(Plugin).Assembly.GetType(type) == null) throw new InvalidOperationException("Integration type was not found: " + type);
        }

        private void Add(string name, string title, bool shared, string[] plugins, params string[] types)
        {
            var group = new Group { Name = name, Title = title, Shared = shared, Plugins = plugins,
                Patches = new IntegrationPatches(Plugin.Id + ".integration." + name) };
            groups.Add(group);
            foreach (string type in types) owners.Add("EpicLootProgressionFix." + type, group);
        }
        private Group Owner(Type type)
        {
            for (; type != null; type = type.DeclaringType) if (owners.TryGetValue(type.FullName, out var group)) return group;
            return null;
        }
        internal static bool Enabled(Type type) => current?.Owner(type) is Group group ? group.Active && !group.Failed : true;
        private bool Allowed(Group group)
        {
            if (group.Failed) return false;
            string problem = group.Plugins.Select(PluginIds.UnavailableReason).FirstOrDefault(reason => reason != null);
            if (problem == null) return true;
            Fail(group, problem);
            return false;
        }
        private static void Snapshot(Group group, Type type)
        {
            if (!group.Snapshots.Add(type)) return;
            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsLiteral || field.IsInitOnly) continue;
                object previous = field.GetValue(null);
                group.Patches.Undo.Add(() => field.SetValue(null, previous));
            }
        }
        internal void Initialize(Type type, Action<IntegrationPatches> initialize, Harmony core)
        {
            var group = Owner(type);
            if (group == null) { initialize(core); return; }
            if (!Allowed(group)) return;
            try
            {
                Snapshot(group, type); Preparing = group.Patches;
                initialize(group.Patches);
            }
            catch (Exception error) { Fail(group, error.GetBaseException().Message); }
            finally { Preparing = null; }
        }
        internal void Collect(Type type, Harmony core)
        {
            var group = Owner(type);
            if (group == null)
            {
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                if (type.IsDefined(typeof(HarmonyPatch), false)) core.CreateClassProcessor(type).Patch();
                return;
            }
            if (!Allowed(group)) return;
            if (group.Active)
            {
                if (type.IsDefined(typeof(HarmonyPatch), false)) throw new InvalidOperationException("Durable integration hooks must be prepared before applying: " + type.Name);
                return;
            }
            try
            {
                Snapshot(group, type); Preparing = group.Patches;
                if (type.IsDefined(typeof(HarmonyPatch), false)) group.Patches.AddClass(type);
            }
            catch (Exception error) { Fail(group, error.GetBaseException().Message); }
            finally { Preparing = null; }
        }
        internal void Apply(bool durableOnly = false)
        {
            foreach (var group in groups.OrderBy(group => group.Plugins.Length))
            {
                if (group.Active || durableOnly && !group.Durable || !Allowed(group)) continue;
                try { group.Patches.Apply(); group.Active = true; applicationOrder.Add(group); }
                catch (Exception error) { Fail(group, error.GetBaseException().Message); }
            }
        }
        private void Fail(Group group, string reason)
        {
            if (group.Failed) return;
            group.Failed = true; group.Active = false;
            // A failed rollback is fatal: do not call this a safely isolated failure.
            group.Patches.Rollback();
            if (group.Plugins.Length == 1) PluginIds.DisableIntegration(group.Plugins[0], reason);
            string warning = group.Title + " unavailable: " + reason;
            logger.LogWarning("Optional integration unavailable: " + warning);
        }
        internal void Rollback()
        {
            // Apply orders dependency groups before combined integrations. Undo in that actual order, otherwise a
            // later snapshot could reinstall an earlier group's hooks after that group had already been removed.
            foreach (var group in applicationOrder.AsEnumerable().Reverse())
                if (!group.Failed && !group.Durable) { group.Patches.Rollback(); group.Active = false; }
            foreach (var group in groups)
                if (!group.Failed && !group.Durable && !applicationOrder.Contains(group)) group.Patches.Rollback();
        }
        internal void Finish()
        {
            foreach (var group in groups.Where(group => group.Active))
                foreach (var cleanup in group.Patches.Cleanup)
                    try { cleanup(); } catch (Exception error) { logger.LogWarning("Initialization backup cleanup: " + error.GetBaseException().Message); }
        }
        internal static string Capabilities => "1\n" + string.Join("\n", current.groups.Where(group => group.Shared).OrderBy(group => group.Name, StringComparer.Ordinal)
            .Select(group => group.Name + "=" + (group.Active ? "on" : "off") + ":" + string.Join(",", group.Plugins.Where(PluginIds.Installed).OrderBy(id => id, StringComparer.Ordinal))));
        internal static string DescribeMismatch(string received)
        {
            var lines = new HashSet<string>((received ?? "").Split('\n'), StringComparer.Ordinal);
            return string.Join(", ", current.groups.Where(group => group.Shared &&
                !lines.Contains(Capabilities.Split('\n').Single(line => line.StartsWith(group.Name + "=", StringComparison.Ordinal)))).Select(group => group.Title));
        }
    }
}
