using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using EpicLoot;
using EpicLoot.ShardStones;
using HarmonyLib;
using Jotunn.Managers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Runes with a yes/no effect have one useful tier. Numeric runes keep their exact tier and power.
    internal static class RuneStacks
    {
        internal static int Limit => Plugin.RuneStackSize.Value;
        private static bool playerReady;
        private static ConditionalWeakTable<Container, Inventory> migratedContainers = new ConditionalWeakTable<Container, Inventory>();
        private static readonly Action<Inventory, bool, bool> Changed = AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(
            AccessTools.Method(typeof(Inventory), "Changed"));
        // Whether an item may join an existing stack: EpicLoot compares the saved magic, and this pack's patches on that
        // check add the rules for runes, favorite markers and single-use rewards.
        internal static readonly Func<ItemDrop.ItemData, ItemDrop.ItemData, bool> StacksWith =
            AccessTools.MethodDelegate<Func<ItemDrop.ItemData, ItemDrop.ItemData, bool>>(AccessTools.Method(typeof(MagicItemData), "StacksWith"));
        private static readonly Action<Container> SaveContainer = AccessTools.MethodDelegate<Action<Container>>(
            AccessTools.Method(typeof(Container), "Save"));
        private const string MagicKey = MagicItemData.Key;
        // Quoted strings are consumed first, so numbers inside names or unknown string metadata remain untouched.
        private static readonly Regex JsonTokens = new Regex("\"(?:\\\\.|[^\"\\\\])*\"|(?<number>-?(?:0|[1-9][0-9]*)(?:\\.[0-9]+)?(?:[eE][+\\-]?[0-9]+)?)");

        private static bool DefinitionsReady => Plugin.Ready && MagicItemEffectDefinitions.AllDefinitions.Count > 0;
        private static bool CanNormalize => DefinitionsReady && ZNet.instance != null && (ZNet.instance.IsServer() || playerReady);
        private static bool Etched(ItemDrop.ItemData.SharedData shared) => shared != null &&
            shared.m_itemType == ItemDrop.ItemData.ItemType.Material && shared.m_ammoType?.EndsWith("|Runestone", StringComparison.Ordinal) == true &&
            (shared.m_name.StartsWith("$mod_epicloot_etched_rune ", StringComparison.Ordinal) || shared.m_name.StartsWith("Etched ", StringComparison.Ordinal));
        internal static bool IsRune(ItemDrop.ItemData item) => item != null && (Etched(item.m_shared) ||
            item.m_dropPrefab != null && item.m_dropPrefab.name.StartsWith("EtchedRunestone", StringComparison.Ordinal) &&
            item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Material && item.m_customData.ContainsKey(MagicKey));

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (PluginIds.TryGet(PluginIds.QuickStack, out var quickStack))
                harmony.Patch(AccessTools.Method(quickStack.Instance.GetType().Assembly.GetType("QuickStackStore.QuickStackModule", true),
                    "GetQuickStackCountIntoThisContainer"), transpiler: new HarmonyMethod(typeof(RuneStacks), nameof(QuickStackMatching)));
            if (PluginIds.TryGet(PluginIds.ItemStacks, out var stacks))
            {
                var processing = stacks.Instance.GetType().Assembly.GetType("ItemStacksItemWeights.ItemProcessing", true);
                harmony.Patch(AccessTools.Method(processing, "PatchShared"),
                    postfix: new HarmonyMethod(typeof(RuneStacks), nameof(AfterStackSettings)));
            }
            IntegrationChanges.Defer(() => PrefabManager.OnPrefabsRegistered += LimitPrefabs, () => PrefabManager.OnPrefabsRegistered -= LimitPrefabs);
            // The server's size can arrive after the prefabs were registered; items share their prefab's limit.
            EventHandler refresh = (sender, arguments) => { if (ObjectDB.instance != null) LimitPrefabs(); };
            IntegrationChanges.Defer(() => Plugin.RuneStackSize.SettingChanged += refresh, () => Plugin.RuneStackSize.SettingChanged -= refresh);
            foreach (var lookup in AccessTools.GetDeclaredMethods(typeof(ObjectDB)).Where(method => method.Name == "GetItemPrefab"))
                harmony.Patch(lookup, postfix: new HarmonyMethod(typeof(RuneStacks), nameof(LoadedPrefab)));
            harmony.Patch(AccessTools.Method(typeof(Game), "SpawnPlayer"),
                postfix: new HarmonyMethod(typeof(RuneStacks), nameof(AfterSpawn)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(ZNet), "Shutdown"),
                prefix: new HarmonyMethod(typeof(RuneStacks), nameof(LeavingWorld)));
            harmony.Patch(AccessTools.Method(typeof(Inventory), "Load", new[] { typeof(ZPackage) }),
                postfix: new HarmonyMethod(typeof(RuneStacks), nameof(AfterInventoryLoad)) { priority = Priority.Last });
            var savedItem = AccessTools.GetDeclaredMethods(typeof(Inventory)).Single(method => method.Name == "AddItem" &&
                method.GetParameters().Length == 14 && method.GetParameters()[0].ParameterType == typeof(int));
            harmony.Patch(savedItem, prefix: new HarmonyMethod(typeof(RuneStacks), nameof(SavedRuneCount)),
                postfix: new HarmonyMethod(typeof(RuneStacks), nameof(RestoreSavedRuneCount)));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Inventory)).Where(method => (method.Name == "AddItem" || method.Name == "CanAddItem") &&
                method.GetParameters().Length > 0 && method.GetParameters()[0].ParameterType == typeof(ItemDrop.ItemData)))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(RuneStacks), nameof(BeforeAdd)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(Container), "GetInventory"),
                postfix: new HarmonyMethod(typeof(RuneStacks), nameof(OwnedContainer)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(Container), "Load"),
                prefix: new HarmonyMethod(typeof(RuneStacks), nameof(ContainerLoading)),
                postfix: new HarmonyMethod(typeof(RuneStacks), nameof(ContainerReloaded)));
            harmony.Patch(AccessTools.Method(typeof(ShardSocketManager), nameof(ShardSocketManager.ReconstructShardItem)),
                postfix: new HarmonyMethod(typeof(RuneStacks), nameof(ReturnedRune)));
            // Runes are decided first; the other rules on this check only refuse.
            harmony.Patch(AccessTools.Method(typeof(MagicItemData), "StacksWith"),
                postfix: new HarmonyMethod(typeof(RuneStacks), nameof(StackRunes)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(InventoryGrid), "DropItem"),
                prefix: new HarmonyMethod(typeof(RuneStacks), nameof(KeepSocketSingle)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(InventoryGrid), "DropItem"),
                prefix: new HarmonyMethod(typeof(RuneStacks), nameof(SwapRunes)) { priority = Priority.Last });
        }

        private static void AfterStackSettings(ItemDrop.ItemData.SharedData __0, ref bool __result)
        {
            if (!Etched(__0)) return;
            __result |= __0.m_maxStackSize != Limit;
            __0.m_maxStackSize = Limit;
        }

        private static IEnumerable<CodeInstruction> QuickStackMatching(IEnumerable<CodeInstruction> instructions)
        {
            var body = instructions.ToList();
            var addItem = AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData) });
            var call = body.Single(instruction => instruction.Calls(addItem));
            call.opcode = OpCodes.Call;
            call.operand = AccessTools.Method(typeof(RuneStacks), nameof(QuickStackRune));
            return body;
        }

        private static bool QuickStackRune(Inventory inventory, ItemDrop.ItemData item)
        {
            if (IsRune(item) && !inventory.GetAllItems().Any(stored => IsRune(stored) &&
                stored.m_shared.m_name == item.m_shared.m_name && stored.m_quality == item.m_quality &&
                stored.m_variant == item.m_variant && stored.m_worldLevel == item.m_worldLevel && StacksWith(stored, item))) return false;
            return inventory.AddItem(item);
        }

        private static void LimitPrefabs()
        {
            foreach (ItemRarity rarity in Enum.GetValues(typeof(ItemRarity)))
            {
                var prefab = PrefabManager.Instance.GetPrefab("EtchedRunestone" + rarity);
                if (prefab != null) prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_maxStackSize = Limit;
            }
        }
        private static void LoadedPrefab(GameObject __result)
        {
            // Inventory deserialization clamps to the prefab limit before it calls AddItem.
            if (__result != null && __result.name.StartsWith("EtchedRunestone", StringComparison.Ordinal))
                __result.GetComponent<ItemDrop>().m_itemData.m_shared.m_maxStackSize = Limit;
        }

        internal static bool Normalize(ItemDrop.ItemData item)
        {
            if (!IsRune(item)) return false;
            item.m_shared.m_maxStackSize = Limit;
            if (!CanNormalize) return false;
            if (!CanonicalMagic(item, out _)) return false;
            // Loading a stackable item's magic makes EpicLoot save it again in its own spelling.
            string saved = item.m_customData[MagicKey];
            var magic = item.GetMagicItem();
            bool normalizedData = item.m_customData[MagicKey] != saved;
            if (magic == null || magic.Effects.Count != 1 || magic.SocketCount != 0 || magic.Sockets.Count != 0 ||
                !string.IsNullOrEmpty(magic.LegendaryID) || !string.IsNullOrEmpty(magic.SetID) || magic.IsUnidentified) return normalizedData;
            var effect = magic.Effects[0];
            if (!MagicItemEffectDefinitions.TryGet(effect.EffectType, out var definition) || !definition.CanBeRunified ||
                definition.Requirements == null || definition.Requirements.NoRoll ||
                !MagicItemEffectDefinitions.IsValuelessEffect(effect.EffectType)) return normalizedData;
            var requirements = definition.Requirements;
            var allowed = Enum.GetValues(typeof(ItemRarity)).Cast<ItemRarity>().Where(rarity =>
                (requirements.AllowedRarities == null || requirements.AllowedRarities.Count == 0 || requirements.AllowedRarities.Contains(rarity)) &&
                (requirements.ExcludedRarities == null || !requirements.ExcludedRarities.Contains(rarity))).ToList();
            // A config change must never turn an old low-tier rune into more valuable sacrifice materials.
            if (allowed.Count == 0 || magic.Rarity < allowed[0]) return normalizedData;
            var prefab = PrefabManager.Instance.GetPrefab("EtchedRunestone" + allowed[0]);
            if (prefab == null) throw new InvalidOperationException("Missing normalized rune prefab: " + allowed[0]);
            bool changed = magic.Rarity != allowed[0] || item.m_dropPrefab != prefab || effect.EffectValue != 1f;
            if (!changed) return normalizedData;
            var normalized = RuneExtractionPower.Deserialize(RuneExtractionPower.Serialize(magic));
            normalized.Rarity = allowed[0];
            normalized.Effects[0].EffectValue = 1f;
            item.m_dropPrefab = prefab;
            item.m_shared = prefab.GetComponent<ItemDrop>().m_itemData.m_shared;
            item.m_shared.m_maxStackSize = Limit;
            item.SaveMagicItem(normalized);
            return true;
        }

        private static string NumberText(string text)
        {
            bool negative = text.StartsWith("-", StringComparison.Ordinal);
            if (negative) text = text.Substring(1);
            var parts = text.Split('e', 'E');
            int exponent = parts.Length == 2 ? int.Parse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture) : 0;
            int dot = parts[0].IndexOf('.');
            string digits = parts[0].Replace(".", "").TrimStart('0');
            if (digits.Length == 0) return "0";
            if (dot >= 0) exponent = checked(exponent - (parts[0].Length - dot - 1));
            while (digits.EndsWith("0", StringComparison.Ordinal)) { digits = digits.Substring(0, digits.Length - 1); exponent = checked(exponent + 1); }
            return (negative ? "-" : "") + digits + (exponent == 0 ? "" : "e" + exponent.ToString(CultureInfo.InvariantCulture));
        }

        private static JToken Ordered(JToken token)
        {
            if (token is JObject obj) return new JObject(obj.Properties().OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => new JProperty(property.Name, Ordered(property.Value))));
            if (token is JArray array) return new JArray(array.Select(Ordered));
            return token.DeepClone();
        }

        private static bool SingleValue(JToken token, out float value)
        {
            value = 0f;
            return token is JRaw && float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !float.IsNaN(value) && !float.IsInfinity(value);
        }

        // Parse and compare copies. Keep unknown JSON members and exact unknown numeric literals; only known effect
        // values/history use the game's Single precision. Object ordering and numeric spelling carry no rune identity.
        internal static bool CanonicalMagic(ItemDrop.ItemData item, out string canonical) =>
            CanonicalMagic(item.m_customData.TryGetValue(MagicKey, out string data) ? data : null, out canonical);

        // Each number becomes a raw literal in its canonical spelling, or with savedText in its exact saved spelling, so that
        // the data writes back unchanged wherever it is left alone.
        internal static JObject ReadPayload(string raw, bool savedText = false)
        {
            using var reader = new JsonTextReader(new StringReader(raw)) { DateParseHandling = DateParseHandling.None };
            var data = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new JsonSerializationException("Unexpected data after the magic item.");
            var numbers = JsonTokens.Matches(raw).Cast<Match>().Where(match => match.Groups["number"].Success).Select(match => match.Value).ToArray();
            var tokens = data.Descendants().Where(token => token.Type == JTokenType.Integer || token.Type == JTokenType.Float).ToArray();
            if (numbers.Length != tokens.Length) throw new JsonSerializationException("Invalid numeric magic item data.");
            for (int index = 0; index < tokens.Length; index++) tokens[index].Replace(new JRaw(savedText ? numbers[index] : NumberText(numbers[index])));
            return data;
        }

        private static bool CanonicalMagic(string raw, out string canonical)
        {
            canonical = null;
            if (string.IsNullOrEmpty(raw)) return false;
            try
            {
                var magic = ReadPayload(raw);
                if (!(magic["Rarity"] is JRaw rarity) || !int.TryParse(rarity.ToString(), out int tier) || !Enum.IsDefined(typeof(ItemRarity), tier) ||
                    !(magic["Effects"] is JArray effects) || effects.Count == 0 && string.IsNullOrEmpty((string)magic["SetID"])) return false;
                foreach (var token in effects)
                {
                    if (!(token is JObject effect) || effect["EffectType"]?.Type != JTokenType.String ||
                        !MagicItemEffectDefinitions.TryGet((string)effect["EffectType"], out _)) return false;
                    bool valueless = MagicItemEffectDefinitions.IsValuelessEffect((string)effect["EffectType"]);
                    if (!SingleValue(effect["EffectValue"], out float value) &&
                        !(valueless && effect["EffectValue"]?.Type == JTokenType.String && (string)effect["EffectValue"] == "NaN")) return false;
                    // Match EpicLoot's existing FixupValuelessEffects, including old NaN-valued yes/no effects.
                    if (valueless) value = 1f;
                    effect["EffectValue"] = new JRaw(NumberText(value.ToString("R", CultureInfo.InvariantCulture)));
                    if (!valueless || effect[RuneExtractionPower.SavedField] != null)
                    {
                        float power = RuneExtractionPower.LegacyRunePower;
                        if (effect[RuneExtractionPower.SavedField] != null && !SingleValue(effect[RuneExtractionPower.SavedField], out power) ||
                            !RuneExtractionPower.ValidPower(power)) return false;
                        effect[RuneExtractionPower.SavedField] = new JRaw(NumberText(power.ToString("R", CultureInfo.InvariantCulture)));
                    }
                }
                // Validate the remaining native fields on a copy before EpicLoot loads either item.
                var parsed = JsonConvert.DeserializeObject<MagicItem>(magic.ToString(Formatting.None));
                if (parsed.Sockets == null || parsed.AugmentedEffectIndices == null || parsed.TemperedEffectIndices == null) return false;
                canonical = Ordered(magic).ToString(Formatting.None);
                return true;
            }
            catch (Exception error) when (error is JsonException || error is FormatException || error is OverflowException || error is InvalidCastException)
            { return false; }
        }

        internal static bool SameMagic(ItemDrop.ItemData left, ItemDrop.ItemData right) =>
            IsRune(left) && IsRune(right) && CanonicalMagic(left, out var first) && CanonicalMagic(right, out var second) && first == second;

        private static bool Migrate(Inventory inventory)
        {
            if (!CanNormalize || inventory == null) return false;
            bool changed = false;
            var items = inventory.GetAllItems();
            foreach (var item in items) changed |= Normalize(item);
            // Keep protected slots, equipped items and quick slots in place. Only consolidate this inventory.
            bool playerInventory = Player.m_localPlayer != null && inventory == Player.m_localPlayer.GetInventory();
            foreach (var source in items.ToArray())
            {
                if (!IsRune(source) || source.m_equipped || source.m_stack <= 0 ||
                    (playerInventory && (source.m_gridPos.y >= (EquipmentAndQuickSlots.VisibleRows?.Invoke() ?? inventory.GetHeight()) || FavoriteProtection.IsFavorite(Player.m_localPlayer, source)))) continue;
                foreach (var target in items)
                {
                    if (source == target) break;
                    if (!IsRune(target) || target.m_equipped || target.m_stack >= Limit || target.m_shared.m_name != source.m_shared.m_name ||
                        target.m_quality != source.m_quality || target.m_variant != source.m_variant || target.m_worldLevel != source.m_worldLevel ||
                        (playerInventory && (target.m_gridPos.y >= (EquipmentAndQuickSlots.VisibleRows?.Invoke() ?? inventory.GetHeight()) || FavoriteProtection.IsFavorite(Player.m_localPlayer, target)))) continue;
                    if (!StacksWith(target, source)) continue;
                    int quantity = Math.Min(source.m_stack, Limit - target.m_stack);
                    target.m_stack += quantity;
                    source.m_stack -= quantity;
                    changed |= quantity > 0;
                    if (source.m_stack == 0) break;
                }
            }
            items.RemoveAll(item => IsRune(item) && item.m_stack == 0);
            foreach (var source in items.Where(item => IsRune(item) && item.m_stack > Limit).ToArray())
            {
                while (source.m_stack > Limit)
                {
                    int rows = playerInventory ? (EquipmentAndQuickSlots.VisibleRows?.Invoke() ?? inventory.GetHeight()) : inventory.GetHeight();
                    var position = new Vector2i(-1, -1);
                    for (int row = 0; row < rows && position.x < 0; row++)
                        for (int column = 0; column < inventory.GetWidth(); column++)
                            if (inventory.GetItemAt(column, row) == null) { position = new Vector2i(column, row); break; }
                    // A full legacy inventory keeps its excess until room exists; never discard or drop player runes.
                    if (position.x < 0) break;
                    var split = source.Clone();
                    split.m_stack = Math.Min(Limit, source.m_stack - Limit);
                    split.m_gridPos = position;
                    source.m_stack -= split.m_stack;
                    items.Add(split);
                    changed = true;
                }
            }
            if (changed) Changed(inventory, false, false);
            return changed;
        }

        private static void AfterSpawn(Game __instance) => __instance.StartCoroutine(ReadyPlayer());
        private static IEnumerator ReadyPlayer()
        {
            // SpawnPlayer's other postfixes finish restoring character favorites before stacks are consolidated.
            yield return null;
            while (Player.m_localPlayer != null && ZNet.instance != null && !DefinitionsReady) yield return null;
            playerReady = Player.m_localPlayer != null;
            if (playerReady) Migrate(Player.m_localPlayer.GetInventory());
        }
        private static void LeavingWorld() => playerReady = false;
        private static void AfterInventoryLoad(Inventory __instance)
        {
            if (Player.m_localPlayer != null && __instance == Player.m_localPlayer.GetInventory()) Migrate(__instance);
        }
        private static void BeforeAdd(ItemDrop.ItemData __0) => Normalize(__0);
        private static void SavedRuneCount(int prefabHash, int stack, out int __state)
        {
            var prefab = ObjectDB.instance.GetItemPrefab(prefabHash);
            __state = stack > Limit && prefab != null && prefab.name.StartsWith("EtchedRunestone", StringComparison.Ordinal) ? stack : 0;
        }
        private static void RestoreSavedRuneCount(Inventory __instance, Vector2i pos, bool __result, int __state)
        {
            if (!__result || __state == 0) return;
            var item = __instance.GetItemAt(pos.x, pos.y);
            if (IsRune(item)) item.m_stack = __state;
        }
        private static void ReturnedRune(ItemDrop.ItemData __result) => Normalize(__result);
        private static void OwnedContainer(Container __instance, Inventory __result)
        {
            // AzuCraftyBoxes can hold a chest that has since unloaded; the destroyed object has nothing to normalize.
            if (!__instance) return;
            var view = GameFields.ContainerView(__instance);
            if (view == null || !view.IsValid() || !view.IsOwner() || !CanNormalize ||
                (migratedContainers.TryGetValue(__instance, out var previous) && previous == __result)) return;
            if (Migrate(__result)) SaveContainer(__instance);
            migratedContainers.Remove(__instance);
            migratedContainers.Add(__instance, __result);
        }
        private static void ContainerLoading(uint ___m_lastRevision, out uint __state) => __state = ___m_lastRevision;
        private static void ContainerReloaded(Container __instance, uint ___m_lastRevision, uint __state)
        {
            if (___m_lastRevision != __state) migratedContainers.Remove(__instance);
        }
        internal static void DefinitionsChanged()
        {
            migratedContainers = new ConditionalWeakTable<Container, Inventory>();
            if (playerReady && Game.instance != null) Game.instance.StartCoroutine(ReadyPlayer());
        }

        // EpicLoot stacks items whose saved magic is spelled identically. Runes stack when their magic means the same,
        // however either is spelled, and all their other data is identical.
        private static void StackRunes(ItemDrop.ItemData existing, ItemDrop.ItemData item, ref bool __result)
        {
            if (existing == item || !IsRune(existing) && !IsRune(item)) return;
            __result = IsRune(existing) && IsRune(item) && existing.m_customData.Count == item.m_customData.Count &&
                CanonicalMagic(existing, out var canonical) && CanonicalMagic(item, out var other) && canonical == other &&
                existing.m_customData.All(entry => item.m_customData.TryGetValue(entry.Key, out string value) &&
                    (entry.Key == MagicKey || value == entry.Value));
        }

        private static bool KeepSocketSingle(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item, Vector2i pos, ref bool __result)
        {
            if (fromInventory != OpenSockets.Row || !IsRune(item)) return true;
            var target = __instance.GetInventory().GetItemAt(pos.x, pos.y);
            if (!IsRune(target) || target.m_stack <= 1 || (target.m_shared.m_name == item.m_shared.m_name &&
                target.m_quality == item.m_quality && target.m_variant == item.m_variant && target.m_worldLevel == item.m_worldLevel &&
                StacksWith(target, item))) return true;
            // Vanilla swapping can otherwise push the entire incompatible stack into a one-item socket.
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Split one rune before swapping it into a socket.");
            __result = false;
            return false;
        }

        // The game swaps a held item with the clicked one only when their names differ or the clicked one cannot stack.
        // Same-tier runes share a name and stack, so a held rune clicked onto one it cannot join stayed held. It now
        // swaps places with it, as other items do; identical runes still merge, and the socket row keeps its own rules.
        private static bool SwapRunes(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item, int amount, Vector2i pos,
            ref bool __result, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var inventory = __instance.GetInventory();
            var target = inventory.GetItemAt(pos.x, pos.y);
            if (target == null || target == item || item.m_stack != amount || !IsRune(item) || !IsRune(target) ||
                target.m_shared.m_name != item.m_shared.m_name || fromInventory == OpenSockets.Row || inventory == OpenSockets.Row ||
                (target.IsSameType(item) && StacksWith(target, item))) return true;
            fromInventory.RemoveItem(item);
            fromInventory.MoveItemToThis(inventory, target, target.m_stack, item.m_gridPos.x, item.m_gridPos.y);
            inventory.MoveItemToThis(fromInventory, item, amount, pos.x, pos.y);
            __result = true;
            return false;
        }
    }
}
