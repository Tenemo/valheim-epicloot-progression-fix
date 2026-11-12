using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using EpicLoot;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    internal static class PersistentFavorites
    {
        private const string StateKey = Plugin.Id + ".favorites";
        private const string ItemKeyPrefix = Plugin.Id + ".favorite.";
        private const string BorderName = "QuickStackStoreBorderImage";
        private static Type userConfig;
        private static FieldInfo itemNames, slots, trashNames, configPath;
        private static MethodInfo getPlayerConfig, consideredTrash, literalTrash;
        private static object playerConfig;
        private static ManualLogSource logger;
        private static Player owner;
        private static FavoriteState state;
        private static HashSet<string> resolvedTypes;
        private static HashSet<string> inheritedTypes;
        private static string itemKey;
        private static bool spawning;
        private static readonly Action<Inventory, bool, bool> InventoryChanged =
            AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(
                AccessTools.Method(typeof(Inventory), "Changed", new[] { typeof(bool), typeof(bool) }));
        private static readonly AccessTools.FieldRef<InventoryGui, InventoryGrid> ContainerGridOf =
            AccessTools.FieldRefAccess<InventoryGui, InventoryGrid>("m_containerGrid");
        private static readonly AccessTools.FieldRef<InventoryGui, Container> ContainerOf =
            AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
        private static readonly Func<InventoryGrid, GameObject, Vector2i> ButtonPosition =
            AccessTools.MethodDelegate<Func<InventoryGrid, GameObject, Vector2i>>(AccessTools.Method(typeof(InventoryGrid), "GetButtonPos"));
        private static Func<InventoryGrid, bool> ignoreClick;
        private static Func<InventoryGrid, Vector2i, bool, bool> handleClick;
        // Quick Stack binds its colors and loads its border sprite in Start, after this plugin's Awake.
        private static AccessTools.FieldRef<ConfigEntry<Color>> favoriteColor;
        private static Func<Image, Image> createBorder;
        private static readonly JsonSerializerSettings jsonSettings = new() { TypeNameHandling = TypeNameHandling.None };
        private static readonly Dictionary<MethodBase, int> expectedCalls = new();
        private static readonly Dictionary<MethodInfo, MethodInfo> replacements = new();

        // An empty record is a completed first load; later joins keep the character's personal choices.
        private sealed class FavoriteState
        {
            public int Version = 1;
            public int EquipmentGeneration;
            public string[] ItemTypes = Array.Empty<string>();
            public int[][] Slots = Array.Empty<int[]>();
            public string[] TrashTypes = Array.Empty<string>();
            // Types whose old per-item markers no longer apply: converted once, or chosen explicitly since.
            public string[] ResolvedTypes = Array.Empty<string>();
            // Imported Quick Stack favorites keep protecting future equipment of that type until explicitly changed.
            public string[] InheritedTypes = Array.Empty<string>();
        }

        internal static string Problem { get; private set; }

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.QuickStack, out var plugin)) return;
            logger = log;
            var assembly = plugin.Instance.GetType().Assembly;
            userConfig = assembly.GetType("QuickStackStore.UserConfig", true);
            itemNames = AccessTools.Field(userConfig, "favoritedItems");
            slots = AccessTools.Field(userConfig, "favoritedSlots");
            trashNames = AccessTools.Field(userConfig, "trashFlaggedItems");
            configPath = AccessTools.Field(userConfig, "_configPath");
            getPlayerConfig = AccessTools.Method(userConfig, "GetPlayerConfig");
            consideredTrash = AccessTools.Method(userConfig, "IsItemNameConsideredTrashFlagged");
            literalTrash = AccessTools.Method(userConfig, "IsItemNameLiterallyTrashFlagged");
            if (itemNames == null || slots == null || trashNames == null || configPath == null || getPlayerConfig == null ||
                consideredTrash == null || literalTrash == null)
                throw new MissingMemberException("Quick Stack favorite storage changed.");

            foreach (var pair in new[] {
                ("IsItemNameFavorited", nameof(IsItemFavorite)),
                ("IsItemNameConsideredTrashFlagged", nameof(IsTrash)),
                ("ToggleItemNameFavoriting", nameof(ToggleFavorite)),
                ("ToggleItemNameTrashFlagging", nameof(ToggleTrash)) })
                replacements.Add(AccessTools.Method(userConfig, pair.Item1), AccessTools.Method(typeof(PersistentFavorites), pair.Item2));

            // Each site must pass the actual ItemData; SharedData alone cannot distinguish two maces.
            foreach (var target in new[] {
                ("UserConfig", "IsItemNameOrSlotFavorited", 1),
                ("InventoryGridButtonHandlingPatches", "HandleClickInternal", 1),
                ("BorderRenderer", "UpdateGui", 2),
                ("TooltipRenderer", "GetTooltip", 2),
                ("SortModule", "ShouldSortItem", 1),
                ("SortModule", "GetAllowedSlots", 1),
                ("TrashModule", "QuickTrash", 1),
                ("TrashModule+TrashItemsPatches", "UpdateItemDrag_Postfix", 3),
                ("TrashModule+TrashItemPlayerPatches", "ShouldAutoPickup", 1) })
            {
                var method = AccessTools.Method(assembly.GetType("QuickStackStore." + target.Item1, true), target.Item2);
                expectedCalls.Add(method, target.Item3);
                harmony.Patch(method, transpiler: Patch(nameof(ItemChecks)));
            }

            harmony.Patch(AccessTools.Method(userConfig, "Load"), prefix: Patch(nameof(EmptyConfig)));
            harmony.Patch(AccessTools.Method(userConfig, "Save"), prefix: Patch(nameof(SaveConfig)));
            harmony.Patch(AccessTools.Method(userConfig, "ToggleSlotFavoriting"), prefix: Patch(nameof(CanEdit)));
            harmony.Patch(AccessTools.Method(userConfig, "ResetAllFavoriting"), prefix: Patch(nameof(ResetFavorites)));
            // Delay the config reset command until the authoritative profile and fresh-character template finish loading.
            var reset = AccessTools.Method(assembly.GetType("QuickStackStore.QSSConfig", true), "ResetAllFavoritingData_SettingChanged");
            harmony.Patch(reset, prefix: Patch(nameof(CanReset)));
            resetCommand = reset;
            harmony.Patch(AccessTools.Method(typeof(Game), "SpawnPlayer"), prefix: Patch(nameof(BeforeSpawn)),
                postfix: new HarmonyMethod(typeof(PersistentFavorites), nameof(AfterSpawn)) {
                    priority = Priority.Last, after = new[] { PluginIds.ServerCharacters } });
            // A full reload must never retain the previous session's cached UserConfig.
            harmony.Patch(AccessTools.Method(typeof(Player), "Load"), prefix: Patch(nameof(BeforeLoad)), postfix: Patch(nameof(AfterLoad)));
            harmony.Patch(AccessTools.Method(typeof(Player), "Save"), prefix: Patch(nameof(BeforeSave)));
            var stacksWith = AccessTools.Method(typeof(MagicItemData), "StacksWith");
            harmony.Patch(stacksWith, postfix: Patch(nameof(CanStack)));
            // The game keeps only a receiving stack's own data, so each place two stacks combine also keeps the markers.
            harmony.Patch(AccessTools.Method(typeof(Inventory), "FindFreeStackItem"), postfix: Patch(nameof(JoinedStack)));
            harmony.Patch(AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) }),
                prefix: Patch(nameof(BeforeCellJoin)), postfix: Patch(nameof(AfterCellJoin)));
            harmony.Patch(AccessTools.Method(typeof(ItemDrop), "AutoStackItems"),
                prefix: new HarmonyMethod(typeof(PersistentFavorites), nameof(StartAutoStack)) { priority = Priority.First }, finalizer: Patch(nameof(EndAutoStack)));
            harmony.Patch(stacksWith, postfix: new HarmonyMethod(typeof(PersistentFavorites), nameof(AutoStackJoin)) { priority = Priority.Last });

            var color = AccessTools.Field(assembly.GetType("QuickStackStore.QSSConfig+FavoriteConfig", true), "BorderColorFavoritedItem");
            var create = AccessTools.Method(assembly.GetType("QuickStackStore.BorderRenderer", true), "CreateBorderImage", new[] { typeof(Image) });
            if (color == null || create == null) throw new MissingMemberException("Quick Stack favorite borders changed.");
            // Every Quick Stack border comes from CreateBorderImage, in the inventory and in containers.
            harmony.Patch(create, postfix: Patch(nameof(WidenBorder)));
            favoriteColor = AccessTools.StaticFieldRefAccess<ConfigEntry<Color>>(color);
            createBorder = AccessTools.MethodDelegate<Func<Image, Image>>(create);
            harmony.Patch(AccessTools.Method(typeof(InventoryGrid), "UpdateGui"), postfix: Patch(nameof(ContainerBorders)));

            var buttons = assembly.GetType("QuickStackStore.InventoryGridButtonHandlingPatches", true);
            var ignore = AccessTools.Method(buttons, "ShouldIgnoreFavoritingClick");
            var handle = AccessTools.Method(buttons, "HandleClickInternal");
            var click = AccessTools.Method(buttons, "HandleClick");
            if (ignore == null || handle == null || click == null) throw new MissingMemberException("Quick Stack favoriting clicks changed.");
            ignoreClick = AccessTools.MethodDelegate<Func<InventoryGrid, bool>>(ignore);
            handleClick = AccessTools.MethodDelegate<Func<InventoryGrid, Vector2i, bool, bool>>(handle);
            harmony.Patch(click, prefix: Patch(nameof(ContainerClick)));
            log.LogInfo("Quick Stack's borders are one pixel wider, and favorites show and can be set in open containers too.");
        }

        private static MethodInfo resetCommand;
        private static HarmonyMethod Patch(string name) => new(typeof(PersistentFavorites), name);
        private static HashSet<string> Names(object config) => (HashSet<string>)itemNames.GetValue(config);
        private static HashSet<string> TrashNames(object config) => (HashSet<string>)trashNames.GetValue(config);
        private static bool Active => owner != null && owner == Player.m_localPlayer && state != null;
        private static bool CanEdit(object __instance) => Active && ReferenceEquals(__instance, playerConfig);
        private static bool CanReset() => Active;

        private static bool EmptyConfig(object __instance)
        {
            itemNames.SetValue(__instance, new HashSet<string>());
            slots.SetValue(__instance, new HashSet<Vector2i>());
            trashNames.SetValue(__instance, new HashSet<string>());
            return false;
        }

        private static void BeforeSpawn() { spawning = true; owner = null; state = null; playerConfig = null; Problem = null; }
        private static void BeforeLoad(Player __instance)
        {
            if (__instance == Player.m_localPlayer) { owner = null; state = null; playerConfig = null; Problem = null; }
        }
        private static void AfterLoad(Player __instance)
        {
            if (!spawning && __instance == Player.m_localPlayer) AfterSpawn();
        }

        private static void AfterSpawn()
        {
            spawning = false;
            var player = Player.m_localPlayer;
            if (player == null) return;
            try
            {
                playerConfig = getPlayerConfig.Invoke(null, new object[] { player.GetPlayerID() });
                EmptyConfig(playerConfig);
                itemKey = ItemKeyPrefix + player.GetPlayerID().ToString(CultureInfo.InvariantCulture);
                bool existing = player.m_customData.TryGetValue(StateKey, out var json);
                FavoriteState loaded;
                if (existing) loaded = JsonConvert.DeserializeObject<FavoriteState>(json, jsonSettings);
                else
                {
                    var imported = QuickStackPreferences.Read((string)configPath.GetValue(playerConfig));
                    loaded = new FavoriteState { ItemTypes = imported.Favorites, Slots = imported.Slots,
                        TrashTypes = imported.Trash, InheritedTypes = imported.Favorites };
                }
                if (loaded == null || loaded.Version != 1 || loaded.EquipmentGeneration < 0 || loaded.ItemTypes == null ||
                    loaded.TrashTypes == null || loaded.ResolvedTypes == null || loaded.Slots == null || loaded.Slots.Any(x => x == null || x.Length != 2) ||
                    loaded.InheritedTypes == null || loaded.InheritedTypes.Any(x => x == null) ||
                    loaded.ItemTypes.Any(x => x == null) || loaded.TrashTypes.Any(x => x == null) || loaded.ResolvedTypes.Any(x => x == null))
                    throw new InvalidDataException("Invalid saved favorites; the original data has been left intact.");
                itemNames.SetValue(playerConfig, new HashSet<string>(loaded.ItemTypes));
                slots.SetValue(playerConfig, new HashSet<Vector2i>(loaded.Slots.Select(x => new Vector2i(x[0], x[1]))));
                trashNames.SetValue(playerConfig, new HashSet<string>(loaded.TrashTypes));
                resolvedTypes = new HashSet<string>(loaded.ResolvedTypes);
                inheritedTypes = new HashSet<string>(loaded.InheritedTypes);
                owner = player;
                state = loaded;
                foreach (var item in player.GetInventory().GetAllItems())
                    if (!Individual(item)) Migrate(item);
                Persist();
                InventoryChanged(player.GetInventory(), false, false);
                resetCommand.Invoke(null, new object[] { null, null });
            }
            catch (Exception error)
            {
                // Only this character is affected: every item counts as favorited, so Quick Stack and Sacrifice
                // leave all items alone and the saved record stays unchanged until it loads.
                owner = null;
                state = null;
                Problem = "Favorites could not load: " + error.GetBaseException().Message;
                logger.LogError(Problem);
            }
        }

        private static string Generation => state.EquipmentGeneration.ToString(CultureInfo.InvariantCulture);

        internal static bool IsMarker(string key) => key.StartsWith(ItemKeyPrefix, StringComparison.Ordinal);

        // Ammo is equipable in Valheim.
        private static bool Equipment(ItemDrop.ItemData item) => item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Ammo &&
            item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.AmmoNonEquipable && item.IsEquipable();

        private static bool Individual(ItemDrop.ItemData item)
        {
            // Ordinary Wishbones carry only their built-in detection effect; other effects or sockets make one individual.
            if (BossRewardStacks.IsWishbone(item)) return BossRewardStacks.Modified(item.GetMagicItem());
            // Bombs and snowballs stack in the game itself. Stack overrides must not turn other equipment into type-wide favorites.
            if (Equipment(item)) return BossRewardStacks.GameStackLimit(item.m_shared) <= 1;
            // Etched runestones can carry rolled effects; plain rarity-colored crafting materials do not.
            return item.GetMagicItem()?.Effects?.Count > 0 || item.m_customData.Keys.Any(IsMarker);
        }

        // Ordinary Wishbones and stackable throwables used per-item markers before their favorites became type-wide.
        // This character's valid marker converts once per type, when the item is carried at load or seen in storage.
        // Choices made since, resets and other characters' markers are left alone.
        private static void Migrate(ItemDrop.ItemData item)
        {
            if (!item.m_customData.TryGetValue(itemKey, out var value) || value != Generation || !resolvedTypes.Add(item.m_shared.m_name)) return;
            // The marker overrode a trash flag for that stack; Quick Stack cannot keep a type both favorite and trash.
            TrashNames(playerConfig).Remove(item.m_shared.m_name);
            Names(playerConfig).Add(item.m_shared.m_name);
            Persist();
        }

        private static void Resolve(ItemDrop.ItemData item)
        {
            if (Equipment(item) && !Individual(item)) resolvedTypes.Add(item.m_shared.m_name);
        }

        private static void CanStack(ItemDrop.ItemData existing, ItemDrop.ItemData item, ref bool __result)
        {
            // EpicLoot compares only the saved magic, so also compare our plain custom-data markers.
            // Otherwise an unmarked etched rune could merge into a marked one and inherit its protection.
            // Ordinary stacks share type-wide favorites, so old markers never keep them apart.
            if (!__result || existing == item || existing.m_customData.Count == 0 && item.m_customData.Count == 0) return;
            bool SameMarkers(ItemDrop.ItemData left, ItemDrop.ItemData right) => left.m_customData
                .Where(pair => IsMarker(pair.Key))
                .All(pair => right.m_customData.TryGetValue(pair.Key, out var value) && value == pair.Value);
            if (!(SameMarkers(existing, item) && SameMarkers(item, existing)) && (Individual(existing) || Individual(item))) __result = false;
        }

        // The merged stack keeps every character's old marker, the latest generation for each, until that character
        // converts it.
        internal static void KeepMarkers(ItemDrop.ItemData stack, ItemDrop.ItemData joined)
        {
            if (stack == null || joined == null || stack == joined) return;
            foreach (var pair in joined.m_customData)
                if (IsMarker(pair.Key) && (!stack.m_customData.TryGetValue(pair.Key, out var kept) || Later(pair.Value, kept)))
                    stack.m_customData[pair.Key] = pair.Value;
        }

        // Adding an item puts it into this stack one unit at a time; EpicLoot names the item being added.
        private static readonly AccessTools.FieldRef<ItemDrop.ItemData> Adding =
            AccessTools.StaticFieldRefAccess<ItemDrop.ItemData>(AccessTools.Field(typeof(MagicItemData), "_adding"));
        private static void JoinedStack(ItemDrop.ItemData __result) => KeepMarkers(__result, Adding());

        // A drag onto a stack adds to the item already in that cell.
        private static void BeforeCellJoin(Inventory __instance, int x, int y, out (ItemDrop.ItemData stack, int count) __state)
        {
            var stack = __instance.GetItemAt(x, y);
            __state = (stack, stack?.m_stack ?? 0);
        }
        private static void AfterCellJoin(ItemDrop.ItemData item, (ItemDrop.ItemData stack, int count) __state)
        {
            if (__state.stack != null && __state.stack.m_stack > __state.count) KeepMarkers(__state.stack, item);
        }

        // EpicLoot combines nearby ground drops right after each check that accepts one.
        private static bool autoStacking;
        private static void StartAutoStack() => autoStacking = true;
        private static void EndAutoStack() => autoStacking = false;
        private static void AutoStackJoin(ItemDrop.ItemData existing, ItemDrop.ItemData item, bool __result)
        {
            if (__result && autoStacking) KeepMarkers(existing, item);
        }

        private static bool Later(string value, string than) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int generation) &&
            (!int.TryParse(than, NumberStyles.Integer, CultureInfo.InvariantCulture, out int other) || generation > other);

        private static bool[] favoriteSlots = new bool[0];

        private static bool IsItemFavorite(object config, ItemDrop.ItemData item)
        {
            if (!Active || !ReferenceEquals(config, playerConfig)) return true;
            if (Individual(item)) return inheritedTypes.Contains(item.m_shared.m_name) ||
                item.m_customData.TryGetValue(itemKey, out var value) && value == Generation;
            Migrate(item);
            return Names(config).Contains(item.m_shared.m_name);
        }

        internal static bool IsFavoriteItem(ItemDrop.ItemData item) => IsItemFavorite(playerConfig, item);

        // Quick Stack asks this for every item of the open inventory on every frame, so its check is bound once per config.
        private static object trashConfig;
        private static Func<ItemDrop.ItemData.SharedData, bool> trashCheck;

        private static bool IsTrash(object config, ItemDrop.ItemData item)
        {
            if (IsItemFavorite(config, item)) return false;
            if (!ReferenceEquals(config, trashConfig))
            {
                trashCheck = AccessTools.MethodDelegate<Func<ItemDrop.ItemData.SharedData, bool>>(consideredTrash, config);
                trashConfig = config;
            }
            return trashCheck(item.m_shared);
        }

        // Quick Stack draws its borders only on the player's own grid, so a favorited item lost its border in a chest or
        // in EpicLoot's socket panel. Draw the favorite border there too, as Quick Stack draws it; slot and trash borders
        // stay on the player's grid. The grid's items are read once a frame, not searched again for each slot.
        private static void ContainerBorders(InventoryGrid __instance, Inventory ___m_inventory, List<InventoryElement> ___m_elements)
        {
            if (InventoryGui.instance == null || __instance != ContainerGridOf(InventoryGui.instance) && !SocketFavorites.Showing(__instance)) return;
            int width = ___m_inventory.GetWidth();
            if (favoriteSlots.Length < ___m_elements.Count) favoriteSlots = new bool[___m_elements.Count];
            Array.Clear(favoriteSlots, 0, ___m_elements.Count);
            foreach (var item in ___m_inventory.GetAllItems())
            {
                int slot = item.m_gridPos.y * width + item.m_gridPos.x;
                if (item.m_gridPos.x >= 0 && item.m_gridPos.x < width && slot >= 0 && slot < ___m_elements.Count &&
                    IsItemFavorite(playerConfig, item))
                    favoriteSlots[slot] = true;
            }
            for (int i = 0; i < ___m_elements.Count; i++)
            {
                bool favorite = favoriteSlots[i];
                var queued = ___m_elements[i].m_queued;
                var found = queued.transform.Find(BorderName);
                if (found == null && !favorite) continue;
                // Set each border once per frame; toggling it off and on again would rebuild the canvas every frame.
                var border = found != null ? found.GetComponent<Image>() : createBorder(queued);
                border.color = favoriteColor().Value;
                border.enabled = favorite;
            }
        }

        private static void WidenBorder(Image __result) => __result.gameObject.AddComponent<WiderBorder>();

        // Quick Stack takes favoriting clicks only on the player's grid, where a right click protects the slot. A left click
        // now favorites an item in an open container too, and in EpicLoot's socket panel, which shows a weapon's stones
        // in a grid of its own; a right click there stays an ordinary click. Quick Stack's HandleClick names its grid
        // parameter __instance, so the arguments are taken by position.
        private static bool ContainerClick(InventoryGrid __0, UIInputHandler __1, bool __2, ref bool __result)
        {
            var gui = InventoryGui.instance;
            if (!__2 || gui == null) return true;
            bool sockets = SocketFavorites.Showing(__0);
            if (!sockets && (__0 != ContainerGridOf(gui) || ContainerOf(gui) == null)) return true;
            // Quick Stack's other checks, asked for the player's grid: teleporting, dragging an item, no favoriting key.
            if (ignoreClick(gui.m_playerGrid)) { __result = true; return false; }
            var position = ButtonPosition(__0, __1.gameObject);
            var item = __0.GetInventory().GetItemAt(position.x, position.y);
            bool marker = item != null && Individual(item);
            if (sockets)
            {
                // The socket row saves its stones onto the weapon whenever it changes, now with the rune's favorite.
                __result = handleClick(__0, position, true);
                if (marker) InventoryChanged(__0.GetInventory(), false, false);
                return false;
            }
            // Only the game that owns a chest saves it, and MultiUserChest opens a chest in use without handing it over.
            if (marker && !ContainerOf(gui).IsOwner())
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Another player is using this chest. Reopen it after they close it to mark its equipment.");
                __result = false;
                return false;
            }
            __result = handleClick(__0, position, true);
            // The marker is part of the chest's item, so save the chest as for any other change to its contents.
            if (marker) InventoryChanged(__0.GetInventory(), false, false);
            return false;
        }

        private static bool ToggleFavorite(object config, ItemDrop.ItemData item)
        {
            if (!CanEdit(config)) return false;
            if (!Individual(item))
            {
                if ((bool)literalTrash.Invoke(config, new object[] { item.m_shared })) return false;
                inheritedTypes.Remove(item.m_shared.m_name);
                if (!Names(config).Remove(item.m_shared.m_name)) Names(config).Add(item.m_shared.m_name);
                Resolve(item);
            }
            else
            {
                // An individual favorite overrides a trash-type rule for this piece only.
                bool wasFavorite = IsItemFavorite(config, item);
                if (inheritedTypes.Remove(item.m_shared.m_name)) Names(config).Remove(item.m_shared.m_name);
                if (wasFavorite) item.m_customData.Remove(itemKey);
                else item.m_customData[itemKey] = Generation;
                InventoryChanged(owner.GetInventory(), false, false);
            }
            Persist();
            return true;
        }

        private static bool ToggleTrash(object config, ItemDrop.ItemData item)
        {
            if (!CanEdit(config) || IsItemFavorite(config, item)) return false;
            if (!TrashNames(config).Remove(item.m_shared.m_name)) TrashNames(config).Add(item.m_shared.m_name);
            Resolve(item);
            Persist();
            return true;
        }

        private static bool ResetFavorites(object __instance)
        {
            if (!CanEdit(__instance)) return false;
            // Invalidate markers on items in chests/tombstones too, without scanning or changing those inventories.
            state.EquipmentGeneration = checked(state.EquipmentGeneration + 1);
            inheritedTypes.Clear();
            return true;
        }

        private static bool SaveConfig(object __instance)
        {
            if (CanEdit(__instance)) Persist();
            return false;
        }

        private static void BeforeSave(Player __instance)
        {
            if (Active && __instance == owner) Persist();
        }

        private static void Persist()
        {
            state.ItemTypes = Names(playerConfig).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            state.TrashTypes = TrashNames(playerConfig).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            state.ResolvedTypes = resolvedTypes.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            state.InheritedTypes = inheritedTypes.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            state.Slots = ((HashSet<Vector2i>)slots.GetValue(playerConfig)).OrderBy(x => x.y).ThenBy(x => x.x)
                .Select(x => new[] { x.x, x.y }).ToArray();
            owner.m_customData[StateKey] = JsonConvert.SerializeObject(state, jsonSettings);
        }

        private static IEnumerable<CodeInstruction> ItemChecks(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            var shared = AccessTools.Field(typeof(ItemDrop.ItemData), "m_shared");
            int changed = 0;
            for (int i = 1; i < code.Count; i++)
            {
                if (code[i].operand is not MethodInfo method || !replacements.TryGetValue(method, out var replacement)) continue;
                if (!code[i - 1].LoadsField(shared)) throw new InvalidOperationException("Quick Stack item argument changed: " + __originalMethod);
                // Keep labels/exception blocks on the original instructions.
                code[i - 1].opcode = OpCodes.Nop;
                code[i - 1].operand = null;
                code[i].opcode = OpCodes.Call;
                code[i].operand = replacement;
                changed++;
            }
            if (changed != expectedCalls[__originalMethod])
                throw new InvalidOperationException("Quick Stack favorite call sites changed: " + __originalMethod);
            return code;
        }
    }

    // Quick Stack's border texture is a 1024-pixel square with a 24-pixel frame, stretched over the slot. A second copy
    // one UI unit further in makes the line one unit wider on the inside: one pixel at 1920 x 1080 with the game's GUI
    // scale at 100%. The copy is part of the border's own mesh, so it keeps the border's color and shows only with it.
    internal sealed class WiderBorder : BaseMeshEffect
    {
        private static readonly List<UIVertex> vertices = new();

        public override void ModifyMesh(VertexHelper mesh)
        {
            if (!IsActive()) return;
            var center = graphic.rectTransform.rect.center;
            vertices.Clear();
            mesh.GetUIVertexStream(vertices);
            for (int i = 0, count = vertices.Count; i < count; i++)
            {
                var vertex = vertices[i];
                vertex.position.x += vertex.position.x < center.x ? 1f : -1f;
                vertex.position.y += vertex.position.y < center.y ? 1f : -1f;
                vertices.Add(vertex);
            }
            mesh.Clear();
            mesh.AddUIVertexTriangleStream(vertices);
        }
    }
}
