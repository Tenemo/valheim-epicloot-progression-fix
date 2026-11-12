using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using EpicLoot;
using EpicLoot.GatedItemType;
using HarmonyLib;
using Newtonsoft.Json;

namespace EpicLootProgressionFix
{
    internal static class CraftingHistory
    {
        private const string StateKey = Plugin.Id + ".crafting-migration";
        private static readonly AccessTools.FieldRef<Player, HashSet<string>> KnownMaterials =
            AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownMaterial");
        private static readonly JsonSerializerSettings JsonSettings = new() { TypeNameHandling = TypeNameHandling.None };
        private static ManualLogSource logger;
        private static Player owner;
        private static PlayerProfile profile;
        private static HashSet<string> legacyCredits;
        private static bool spawning;
        private static bool freshCharacter;
        private static ZNetScene conversionScene;
        private static Dictionary<string, HashSet<string>> conversionSources;
        internal static string Problem { get; private set; }

        private sealed class MigrationRecord
        {
            [JsonProperty(Required = Required.Always)] public int Version = 1;
            [JsonProperty(Required = Required.Always)] public string[] Items = Array.Empty<string>();
        }

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            logger = log;
            harmony.Patch(AccessTools.Method(typeof(Game), "SpawnPlayer"), prefix: Patch(nameof(BeforeSpawn)),
                postfix: new HarmonyMethod(typeof(CraftingHistory), nameof(AfterSpawn)) {
                    priority = Priority.Last, after = new[] { PluginIds.ServerCharacters } },
                finalizer: Patch(nameof(SpawnFailed)));
            harmony.Patch(AccessTools.Method(typeof(Player), "Load"), prefix: Patch(nameof(BeforeLoad)),
                postfix: Patch(nameof(AfterLoad)));
            harmony.Patch(AccessTools.Method(typeof(Player), "Save"), prefix: Patch(nameof(BeforeSave)));
        }

        private static HarmonyMethod Patch(string name) => new(typeof(CraftingHistory), name);

        private static void Reset()
        {
            owner = null;
            profile = null;
            legacyCredits = null;
            Problem = null;
        }

        private static void BeforeSpawn()
        {
            Reset();
            spawning = true;
            freshCharacter = GameFields.ProfileData(Game.instance.GetPlayerProfile()) == null ||
                ServerCharacterTemplate.Fresh;
        }

        private static void AfterSpawn()
        {
            spawning = false;
            Attach(freshCharacter);
        }

        private static void SpawnFailed(Exception __exception)
        {
            if (__exception == null) return;
            spawning = false;
            Reset();
        }

        private static void BeforeLoad(Player __instance)
        {
            if (__instance == Player.m_localPlayer) Reset();
        }

        private static void AfterLoad(Player __instance)
        {
            if (!spawning && __instance == Player.m_localPlayer) Attach(false);
        }

        private static void BeforeSave(Player __instance)
        {
            // ServerCharacters saves its fresh template before our SpawnPlayer postfix runs.
            if (spawning && freshCharacter && __instance == Player.m_localPlayer && !__instance.m_customData.ContainsKey(StateKey))
                __instance.m_customData[StateKey] = JsonConvert.SerializeObject(new MigrationRecord(), JsonSettings);
        }

        private static bool CreationRecipe(Recipe recipe) => recipe != null && recipe.m_item != null &&
            recipe.m_amount > 0 && (recipe.m_craftingStation == null || !recipe.m_craftingStation.m_upgrader);

        private static void Attach(bool fresh)
        {
            Reset();
            var player = Player.m_localPlayer;
            if (player == null) return;
            try
            {
                var currentProfile = Game.instance.GetPlayerProfile();
                if (currentProfile.GetPlayerID() != player.GetPlayerID())
                    throw new InvalidOperationException("The loaded character does not match its crafting profile.");
                HashSet<string> credits;
                if (player.m_customData.TryGetValue(StateKey, out var json))
                {
                    var record = JsonConvert.DeserializeObject<MigrationRecord>(json, JsonSettings);
                    if (record == null || record.Version != 1 || record.Items == null || record.Items.Any(string.IsNullOrEmpty))
                        throw new InvalidDataException("Invalid saved crafting migration; original data has been left intact.");
                    credits = new HashSet<string>(record.Items, StringComparer.Ordinal);
                }
                else
                {
                    credits = new HashSet<string>(StringComparer.Ordinal);
                    if (!fresh)
                    {
                        var database = ObjectDB.instance;
                        if (database == null || !database.m_recipes.Any(CreationRecipe))
                            throw new InvalidOperationException("Crafting recipes are unavailable; migration was not saved.");
                        // Disabled/seasonal recipes still describe craftable items. Current recipe knowledge is irrelevant.
                        foreach (var recipe in database.m_recipes.Where(CreationRecipe))
                        {
                            var item = recipe.m_item.m_itemData;
                            if (EpicLoot.EpicLoot.IsAllowedMagicItemType(item) && KnownMaterials(player).Contains(item.m_shared.m_name) &&
                                !LootDenyList.IsDenied(recipe.m_item.gameObject.name))
                                credits.Add(item.m_shared.m_name);
                        }
                    }
                    player.m_customData[StateKey] = JsonConvert.SerializeObject(new MigrationRecord {
                        Items = credits.OrderBy(name => name, StringComparer.Ordinal).ToArray() }, JsonSettings);
                    logger.LogInfo($"Crafting migration saved {credits.Count} legacy credits for this character.");
                }
                owner = player;
                profile = currentProfile;
                legacyCredits = credits;
            }
            catch (Exception error)
            {
                // Only this character is affected: its crafted-equipment drops stay locked until the record loads.
                Problem = "Crafting history could not load: " + error.GetBaseException().Message;
                logger.LogError(Problem);
            }
        }

        internal static bool Ready => Player.m_localPlayer != null && Player.m_localPlayer == owner && profile != null && legacyCredits != null &&
            Game.instance.GetPlayerProfile() == profile && Player.m_localPlayer.GetPlayerID() == profile.GetPlayerID();

        internal static bool IsUnlocked(string prefabName)
        {
            var player = Player.m_localPlayer;
            if (string.IsNullOrEmpty(prefabName) || !Ready) return false;
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) return false;
            string name = drop.m_itemData.m_shared.m_name;
            var crafts = profile.m_playerStats[0].m_itemCraftStats;
            bool Crafted(string item) => crafts.TryGetValue(item, out float count) && count > 0;
            bool HasCreationRecipe() =>
                ObjectDB.instance.m_recipes.Any(recipe => CreationRecipe(recipe) && recipe.m_item.m_itemData.m_shared.m_name == name);
            if (legacyCredits.Contains(name)) return true;
            // A cooking station such as the Frost Foundry credits its finished item to whoever collects it, and older saves
            // to whoever owned the station. The cast is crafted normally, so its craft credits the player who made it.
            if (ConversionSources().TryGetValue(name, out var casts))
                return casts.Any(Crafted) || (Crafted(name) && HasCreationRecipe());
            if (Crafted(name)) return true;
            // The rod is purchased. A mod that adds a creation recipe makes it subject to crafting history too.
            return prefabName == "FishingRod" && KnownMaterials(player).Contains(name) && !HasCreationRecipe();
        }

        private static Dictionary<string, HashSet<string>> ConversionSources()
        {
            var scene = ZNetScene.instance;
            if (conversionSources != null && conversionScene == scene) return conversionSources;
            var sources = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            if (scene != null)
            {
                foreach (var prefab in scene.m_prefabs)
                {
                    if (prefab == null) continue;
                    foreach (var station in prefab.GetComponentsInChildren<CookingStation>(true))
                    foreach (var conversion in station.m_conversion ?? new List<CookingStation.ItemConversion>())
                    {
                        if (conversion?.m_from == null || conversion.m_to == null) continue;
                        string finished = conversion.m_to.m_itemData.m_shared.m_name;
                        if (!sources.TryGetValue(finished, out var casts))
                            sources[finished] = casts = new HashSet<string>(StringComparer.Ordinal);
                        casts.Add(conversion.m_from.m_itemData.m_shared.m_name);
                    }
                }
            }
            conversionScene = scene;
            conversionSources = sources;
            return sources;
        }
    }

    [HarmonyPatch(typeof(GatedItemTypeHelper), "CheckIfItemNeedsGate", new[] { typeof(GatedItemTypeMode), typeof(string) })]
    internal static class CraftedEquipmentGate
    {
        private static bool Prefix(GatedItemTypeMode mode, string itemName, ref bool __result)
        {
            if (mode != GatedItemTypeMode.PlayerMustHaveCraftedItem || (Player.m_localPlayer == null && TreasureProgression.Current == null)) return true;
            __result = !Eligibility.IsCraftingEligible(itemName);
            return false;
        }
    }
}
