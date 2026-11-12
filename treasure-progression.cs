using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using EpicLoot;
using EpicLoot.Adventure;
using EpicLoot.Adventure.Feature;
using EpicLoot.GatedItemType;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class TreasureProgression
    {
        private const string HistoryKey = Plugin.Id + ".treasure-history";
        private const string CheckedKey = Plugin.Id + ".treasure-checked";
        private const int Version = 1;
        private static readonly int TreasureBiomeKey = "TreasureMapChest.Biome".GetStableHashCode();
        private static readonly int FoundKey = "TreasureMapChest.HasBeenFound".GetStableHashCode();
        private static readonly Func<Container, bool> Load = AccessTools.MethodDelegate<Func<Container, bool>>(AccessTools.Method(typeof(Container), "Load"));
        private static readonly Action<Container> Save = AccessTools.MethodDelegate<Action<Container>>(AccessTools.Method(typeof(Container), "Save"));
        private static readonly Action<Inventory, bool, bool> Changed = AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(AccessTools.Method(typeof(Inventory), "Changed"));
        private delegate bool ReadTreasure(ZDO data, out TreasureMapChestInfo treasure);
        private static ReadTreasure readTreasure;
        private static ManualLogSource logger;
        private static readonly HashSet<ZDOID> Waiting = new();
        [ThreadStatic] internal static BuyerHistory Current;

        internal sealed class BuyerHistory
        {
            internal readonly long BuyerId;
            internal readonly HashSet<string> Unlocked;
            internal BuyerHistory(long buyerId, IEnumerable<string> unlocked)
            {
                BuyerId = buyerId;
                Unlocked = new HashSet<string>(unlocked, StringComparer.Ordinal);
            }
            internal byte[] Encode()
            {
                var package = new ZPackage();
                package.Write(Version);
                package.Write(BuyerId);
                package.Write(Unlocked.Count);
                foreach (string item in Unlocked.OrderBy(item => item, StringComparer.Ordinal)) package.Write(item);
                return package.GetArray();
            }
            internal static BuyerHistory Decode(byte[] bytes, long buyerId)
            {
                if (bytes == null) return null;
                if (bytes.Length > 65536) throw new InvalidDataException("Treasure crafting history is too large.");
                var package = new ZPackage(bytes);
                if (package.ReadInt() != Version || package.ReadLong() != buyerId)
                    throw new InvalidDataException("Treasure crafting history does not match its buyer or version.");
                int count = package.ReadInt();
                if (count < 0 || count > 4096) throw new InvalidDataException("Invalid treasure crafting-history count.");
                var names = new List<string>(count);
                for (int index = 0; index < count; index++)
                {
                    string name = package.ReadString();
                    if (string.IsNullOrEmpty(name) || name.Length > 128) throw new InvalidDataException("Invalid treasure equipment name.");
                    names.Add(name);
                }
                if (package.GetPos() != package.Size()) throw new InvalidDataException("Unexpected treasure crafting-history data.");
                return new BuyerHistory(buyerId, names);
            }
        }

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            logger = log;
            var spawner = typeof(TreasureMapChest).Assembly.GetType("EpicLoot.Adventure.AdventureSpawnController", true);
            readTreasure = AccessTools.MethodDelegate<ReadTreasure>(AccessTools.Method(spawner, "TryReadTreasure"));
            harmony.Patch(AccessTools.Method(typeof(TreasureMapsAdventureFeature), "CreateTreasureSpawner"),
                prefix: new HarmonyMethod(typeof(TreasureProgression), nameof(BeforePurchase)));
            harmony.Patch(AccessTools.Method(spawner, "SetTreasure"),
                prefix: new HarmonyMethod(typeof(TreasureProgression), nameof(RecordBuyer)));
            harmony.Patch(AccessTools.Method(spawner, "Update"),
                prefix: new HarmonyMethod(typeof(TreasureProgression), nameof(PrepareLegacySpawner)));
            harmony.Patch(AccessTools.Method(spawner, "SpawnChest"),
                prefix: new HarmonyMethod(typeof(TreasureProgression), nameof(BeforeSpawn)),
                finalizer: new HarmonyMethod(typeof(TreasureProgression), nameof(EndScope)));
            harmony.Patch(AccessTools.Method(typeof(TreasureMapChest), nameof(TreasureMapChest.Setup)),
                prefix: new HarmonyMethod(typeof(TreasureProgression), nameof(BeforeSetup)),
                postfix: new HarmonyMethod(typeof(TreasureProgression), nameof(AfterSetup)),
                finalizer: new HarmonyMethod(typeof(TreasureProgression), nameof(EndScope)));
            harmony.Patch(AccessTools.Method(typeof(Container), nameof(Container.GetInventory)),
                prefix: new HarmonyMethod(typeof(TreasureProgression), nameof(BeforeInventory)) { priority = Priority.First });
            foreach (string method in new[] { "RPC_OpenResponse", "RPC_TakeAllResponse" })
                harmony.Patch(AccessTools.Method(typeof(Container), method),
                    prefix: new HarmonyMethod(typeof(TreasureProgression), nameof(BeforeCollection)) { priority = Priority.First });
        }

        private static BuyerHistory Capture(long buyerId)
        {
            if (!CraftingHistory.Ready || Player.m_localPlayer.GetPlayerID() != buyerId) return null;
            return new BuyerHistory(buyerId, GatedItemTypeHelper.AllItemsWithDetails.Keys.Where(CraftingHistory.IsUnlocked));
        }

        private static bool BeforePurchase(ref bool __result)
        {
            if (!Eligibility.CraftingMode || Capture(Player.m_localPlayer?.GetPlayerID() ?? 0) != null) return true;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Treasure purchase is waiting for your crafting history to load.");
            logger.LogError("Treasure purchase refused before payment: the buyer's crafting history is unavailable.");
            __result = false;
            return false;
        }

        private static void RecordBuyer(MonoBehaviour __instance, TreasureMapChestInfo __0)
        {
            if (!Eligibility.CraftingMode) return;
            var history = Capture(__0.PlayerID) ?? throw new InvalidOperationException("Treasure spawner must capture its actual buyer's crafting history.");
            __instance.GetComponent<ZNetView>().GetZDO().Set(HistoryKey, history.Encode());
        }

        // Old spawners have no purchase snapshot. Their buyer supplies it when their area loads.
        private static bool PrepareLegacySpawner(MonoBehaviour __instance)
        {
            if (!Eligibility.CraftingMode) return true;
            var view = __instance.GetComponent<ZNetView>();
            if (!view || !view.IsValid() || !readTreasure(view.GetZDO(), out var treasure) || treasure.PlayerID == 0) return true;
            var data = view.GetZDO();
            if (data.GetByteArray(HistoryKey) != null) return true;
            var history = Capture(treasure.PlayerID);
            if (history == null)
            {
                if (view.IsOwner() && Waiting.Add(data.m_uid)) logger.LogWarning($"Legacy {treasure.Biome} treasure {treasure.Interval + 1} waits for buyer {treasure.PlayerID}'s crafting history.");
                return false;
            }
            view.ClaimOwnership();
            data.Set(HistoryKey, history.Encode());
            Waiting.Remove(data.m_uid);
            return true;
        }

        private static bool BeforeSpawn(MonoBehaviour __instance, TreasureMapChestInfo __0, out BuyerHistory __state)
        {
            __state = Current;
            if (!Eligibility.CraftingMode) return true;
            var data = __instance.GetComponent<ZNetView>().GetZDO();
            try
            {
                var history = BuyerHistory.Decode(data.GetByteArray(HistoryKey), __0.PlayerID);
                if (history == null) return false;
                Current = history;
                return true;
            }
            catch (Exception error)
            {
                if (Waiting.Add(data.m_uid)) logger.LogError("Treasure generation deferred: " + error.GetBaseException().Message);
                return false;
            }
        }

        private static bool BeforeSetup(TreasureMapChest __instance, long playerID, out BuyerHistory __state)
        {
            __state = Current;
            if (!Eligibility.CraftingMode) return true;
            var history = Current?.BuyerId == playerID ? Current : Capture(playerID);
            if (history == null) throw new InvalidOperationException("Treasure setup has no crafting history for its buyer.");
            Current = history;
            __instance.GetComponent<ZNetView>().GetZDO().Set(HistoryKey, history.Encode());
            return true;
        }

        private static void AfterSetup(TreasureMapChest __instance, bool __runOriginal)
        {
            if (__runOriginal && Eligibility.CraftingMode)
                __instance.GetComponent<ZNetView>().GetZDO().Set(CheckedKey, Version);
        }

        private static void EndScope(BuyerHistory __state) => Current = __state;

        private static bool Legacy(Container chest, out ZNetView view, out ZDO data)
        {
            view = chest ? GameFields.ContainerView(chest) : null;
            data = view && view.IsValid() ? view.GetZDO() : null;
            return Eligibility.CraftingMode && Current == null && data != null &&
                !string.IsNullOrEmpty(data.GetString(TreasureBiomeKey)) && !data.GetBool(FoundKey) && data.GetInt(CheckedKey) != Version;
        }

        private static void BeforeInventory(Container __instance)
        {
            if (Legacy(__instance, out _, out var data) && Player.m_localPlayer != null &&
                Player.m_localPlayer.GetPlayerID() == data.GetLong(ZDOVars.s_creator) && !Repair(__instance))
                throw new InvalidOperationException("Treasure collection is waiting for its buyer's equipment correction.");
        }

        private static bool BeforeCollection(Container __instance, ref bool granted)
        {
            if (!granted || !Legacy(__instance, out _, out _)) return true;
            if (Repair(__instance)) return true;
            granted = false;
            return false;
        }

        private static bool Repair(Container chest)
        {
            if (!Legacy(chest, out var view, out var data)) return true;
            var history = Capture(data.GetLong(ZDOVars.s_creator));
            if (history == null)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "This treasure needs its buyer's crafting history before it can be collected.");
                return false;
            }
            var previous = Current;
            Inventory inventory = null;
            ItemDrop.ItemData[] originalItems = null;
            byte[] originalSaved = null;
            bool replaced = false;
            try
            {
                var savedHistory = data.GetByteArray(HistoryKey);
                if (savedHistory != null) history = BuyerHistory.Decode(savedHistory, history.BuyerId);
                view.ClaimOwnership();
                if (!view.IsOwner()) throw new InvalidOperationException("Treasure ownership was not granted to its buyer.");
                Current = history;
                Load(chest);
                inventory = GameFields.ContainerInventory(chest);
                var replacements = new List<(int Index, ItemDrop.ItemData Item)>();
                var items = inventory.GetAllItems();
                originalItems = items.ToArray();
                originalSaved = data.GetByteArray(ZDOVars.s_items)?.ToArray();
                for (int index = 0; index < items.Count; index++)
                {
                    var original = items[index];
                    if (!EpicLoot.EpicLoot.IsAllowedMagicItemType(original) || original.IsUnidentified() ||
                        history.Unlocked.Contains(original.m_dropPrefab.name)) continue;
                    var magic = original.GetMagicItem();
                    if (magic != null && (magic.Sockets.Any(socket => socket != null) || magic.AugmentedEffectIndices.Count > 0 || magic.TemperedEffectIndices.Count > 0))
                        throw new InvalidDataException("Unopened treasure contains edited equipment; its original contents were preserved.");
                    var candidates = Eligibility.Candidates(original.m_dropPrefab.name);
                    if (candidates.Count == 0) throw new InvalidOperationException("Craft eligible equipment before collecting this treasure; its original contents were preserved.");
                    string selected = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                    ItemDrop.ItemData replacement;
                    if (magic == null) replacement = ObjectDB.instance.GetItemPrefab(selected).GetComponent<ItemDrop>().m_itemData.Clone();
                    else
                    {
                        var table = LootTable.Simple("Treasure progression", new[] { new[] { 1f, 100f } }, new[] { new LootDrop { Item = selected, Weight = 1 } });
                        var rolled = LootRoller.RollLootNoTableWithSpecifics(chest.transform.position, new List<LootTable> { table }, 1,
                            original.GetRarity(), false, 1, 1f);
                        if (rolled.Count != 1) throw new InvalidOperationException("Treasure replacement did not produce exactly one eligible item.");
                        replacement = rolled[0];
                    }
                    replacement.m_gridPos = original.m_gridPos;
                    replacement.m_stack = original.m_stack;
                    replacement.m_quality = original.m_quality;
                    replacements.Add((index, replacement));
                }
                foreach (var replacement in replacements) items[replacement.Index] = replacement.Item;
                replaced = replacements.Count > 0;
                if (replacements.Count > 0) Changed(inventory, true, true);
                Save(chest);
                data.Set(HistoryKey, history.Encode());
                data.Set(CheckedKey, Version);
                Waiting.Remove(data.m_uid);
                if (replacements.Count > 0) logger.LogInfo($"Corrected {replacements.Count} unopened treasure equipment item(s) for buyer {history.BuyerId}; their rarities and other contents were retained.");
                return true;
            }
            catch (Exception error)
            {
                if (replaced)
                {
                    var items = inventory.GetAllItems();
                    items.Clear();
                    items.AddRange(originalItems);
                    if (originalSaved != null) data.Set(ZDOVars.s_items, originalSaved);
                    else Save(chest);
                }
                if (Waiting.Add(data.m_uid)) logger.LogError("Treasure correction failed: " + error.GetBaseException().Message);
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, error.GetBaseException().Message);
                return false;
            }
            finally { Current = previous; }
        }
    }
}
