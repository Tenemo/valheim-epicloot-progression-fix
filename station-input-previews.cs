using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Predictions only: no interactions, patched ingredient finders, payment, ownership or placement calls.
    [HarmonyPatch]
    internal static class StationInputPreviews
    {
        private static MethodInfo query, chestName, itemCount;
        private static Func<bool> prevented;
        private static Func<string, string, bool> canPull;
        private static Func<int, int> leaveOne;
        private static ConfigEntry<float> range;
        private static ConfigEntry<KeyboardShortcut> fillKey;
        internal static Func<bool> ShowCookingTimers;
        internal static Func<CookingStation, string> CookingTimers;
        private static readonly Func<CookingStation, bool> FireLit = AccessTools.MethodDelegate<Func<CookingStation, bool>>(AccessTools.Method(typeof(CookingStation), "IsFireLit"));
        private static readonly Func<Smelter, string, bool> OreAllowed = AccessTools.MethodDelegate<Func<Smelter, string, bool>>(AccessTools.Method(typeof(Smelter), "IsItemAllowed", new[] { typeof(string) }));
        private static readonly Func<Turret, int> AmmoCount = AccessTools.MethodDelegate<Func<Turret, int>>(AccessTools.Method(typeof(Turret), "GetAmmo"));
        private static readonly Func<Turret, string> AmmoType = AccessTools.MethodDelegate<Func<Turret, string>>(AccessTools.Method(typeof(Turret), "GetAmmoType"));
        private static readonly Func<Turret, string, bool> AmmoAllowed = AccessTools.MethodDelegate<Func<Turret, string, bool>>(AccessTools.Method(typeof(Turret), "IsItemAllowed"));
        private static readonly Regex Tags = new Regex("<[^>]+>");

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var boxes = assembly.GetType("AzuCraftyBoxes.Util.Functions.Boxes", true);
            var container = assembly.GetType("AzuCraftyBoxes.IContainers.IContainer", true);
            query = AccessTools.Method(AccessTools.Inner(boxes, "QueryFrame"), "Get").MakeGenericMethod(typeof(Component));
            chestName = AccessTools.Method(container, "GetPrefabName"); itemCount = AccessTools.Method(container, "ItemCount");
            canPull = AccessTools.MethodDelegate<Func<string, string, bool>>(AccessTools.Method(assembly.GetType("AzuCraftyBoxes.API", true), "CanItemBePulled", new[] { typeof(string), typeof(string) }));
            leaveOne = AccessTools.MethodDelegate<Func<int, int>>(AccessTools.Method(boxes, "CheckAndDecrement"));
            prevented = AccessTools.MethodDelegate<Func<bool>>(AccessTools.Method(assembly.GetType("AzuCraftyBoxes.Util.Functions.MiscFunctions", true), "ShouldPrevent"));
            fillKey = (ConfigEntry<KeyboardShortcut>)AccessTools.Field(plugin.Instance.GetType(), "fillAllModKey").GetValue(null);
            if (chestName == null || itemCount == null || fillKey == null || !plugin.Instance.Config.TryGetEntry("2 - CraftyBoxes", "Container Range", out range))
                throw new InvalidOperationException("AzuCraftyBoxes' station preview inputs changed.");
            foreach (var target in new[] {
                ("OverrideHoverText", "UpdateAddOreSwitchHoverText"), ("OverrideHoverText", "UpdateAddWoodSwitchHoverText"),
                ("OverrideHoverTextSg", "UpdateAddFuelSwitchHoverText"), ("FireplaceGetHoverTextPatch", "Postfix"), ("TurretGetHoverTextPatch", "Postfix") })
                harmony.Patch(AccessTools.Method(assembly.GetType("AzuCraftyBoxes.Patches." + target.Item1, true), target.Item2), prefix: new HarmonyMethod(typeof(StationInputPreviews), nameof(ReplaceWriter)));
        }
        private static bool ReplaceWriter() => false;
        private static string Local(string text) => Localization.instance.Localize(text ?? "");
        private static string Name(ItemDrop item) => Local(item.m_itemData.m_shared.m_name);
        private static string Name(ItemDrop.ItemData item) => Local(item.m_shared.m_name);
        private static bool Valid(Component target) => Player.m_localPlayer && target && target.GetComponentInParent<ZNetView>() is ZNetView view && view.IsValid();
        private static bool Denied(string text) => text.Contains("$piece_noaccess") || text.Contains(Local("$piece_noaccess"));
        private static bool OwnModifier(out float radius) => BulkPickup.TryGetHarvestRadius(out radius, true) && BulkPickup.Modifier != KeyCode.None;
        private static string Use(string action) => Local("[<color=yellow><b>$KEY_Use</b></color>] " + action);
        private static string Bulk(string action) => BulkPickup.Hint(action).TrimStart('\n');
        private static string FillKey(bool mainOnly) => mainOnly ? fillKey.Value.MainKey.ToString() : fillKey.Value.ToString();
        private static string FillHint(string action, bool mainOnly = false) => Local("[<color=yellow><b>" + FillKey(mainOnly) + " + $KEY_Use</b></color>] " + action);
        private static bool ShowFill(bool own, bool mainOnly = false)
        {
            if (prevented() || fillKey.Value.MainKey == KeyCode.None) return false;
            if (!own) return true;
            var keys = mainOnly ? new[] { fillKey.Value.MainKey } : fillKey.Value.Modifiers.Concat(new[] { fillKey.Value.MainKey });
            var bulk = BulkPickup.Modifier;
            return !keys.Any(key => key == bulk || (bulk == KeyCode.LeftShift || bulk == KeyCode.RightShift) && (key == KeyCode.LeftShift || key == KeyCode.RightShift));
        }

        // Each hover shares its eligible chest lists. Simulated spends are local and reset between action predictions.
        private sealed class Supplies
        {
            internal readonly Player Player = Player.m_localPlayer;
            internal Inventory Bag => Player.GetInventory();
            private readonly Dictionary<Component, object[]> nearby = new Dictionary<Component, object[]>();
            private readonly Dictionary<(object, string), int> spent = new Dictionary<(object, string), int>();
            internal bool Crafty => !prevented();
            internal void Reset() => spent.Clear();
            internal object[] Chests(Component origin)
            {
                if (!Crafty) return Array.Empty<object>();
                if (!nearby.TryGetValue(origin, out var found))
                    nearby.Add(origin, found = ((IEnumerable)query.Invoke(null, new object[] { origin, range.Value })).Cast<object>().ToArray());
                return found;
            }
            private int Used(object source, string name) => spent.TryGetValue((source, name), out int value) ? value : 0;
            internal int BagCount(ItemDrop item) => Math.Max(0, Bag.CountItems(item.m_itemData.m_shared.m_name) - Used(Bag, item.m_itemData.m_shared.m_name));
            internal int RawCount(object chest, ItemDrop item) => Math.Max(0, (int)itemCount.Invoke(chest, new object[] { item.m_itemData.m_shared.m_name }) - Used(chest, item.m_itemData.m_shared.m_name));
            internal int ChestCount(object chest, ItemDrop item) => canPull((string)chestName.Invoke(chest, null), item.name) ? Math.Max(0, leaveOne(RawCount(chest, item))) : 0;
            // An ordinary use's or a fill's whole stock of an item, before any simulated spending: every bag stack, and what
            // each chest AzuCraftyBoxes pulls from for this station may give.
            internal int Stock(Component origin, Component station, ItemDrop item)
            {
                string name = item.m_itemData.m_shared.m_name;
                int total = Bag.CountItems(name);
                if (BulkProcessing.CanPull(station, item.name))
                    foreach (var chest in Chests(origin))
                        if (canPull((string)chestName.Invoke(chest, null), item.name)) total += Math.Max(0, leaveOne((int)itemCount.Invoke(chest, new object[] { name })));
                return total;
            }
            internal int Take(object source, ItemDrop item, int amount)
            {
                if (amount > 0) { string name = item.m_itemData.m_shared.m_name; spent[(source, name)] = Used(source, name) + amount; }
                return amount;
            }
            internal ItemDrop ChestFirst(Component origin, ItemDrop item, bool stopAtFirstMatch = false)
            {
                foreach (var chest in Chests(origin))
                {
                    if (RawCount(chest, item) < 1) continue;
                    if (ChestCount(chest, item) > 0) return item;
                    if (stopAtFirstMatch) return null;
                }
                return null;
            }
            internal int Fill(Component origin, ItemDrop item, int capacity, bool bag)
            {
                if (capacity <= 0 || !canPull(Utils.GetPrefabName(origin.gameObject), item.name)) return 0;
                int total = bag ? Take(Bag, item, Math.Min(capacity, BagCount(item))) : 0;
                foreach (var chest in Chests(origin))
                {
                    int amount = Math.Min(capacity - total, ChestCount(chest, item));
                    total += Take(chest, item, amount);
                    if (total >= capacity) break;
                }
                return total;
            }
        }
        // Each part's amount, then the stock it is taken from: Fill: 10 x Tin Ore (739).
        private static string Parts(List<(ItemDrop Item, int Amount, int Stock)> parts) =>
            string.Join(", ", parts.Where(part => part.Amount > 0).Select(part => part.Amount + " x " + Stocked(part.Item, part.Stock)));
        private static string Stocked(ItemDrop item, int stock) => Name(item) + " (" + stock + ")";
        private static string Selected(string verb, ItemDrop item, Func<ItemDrop, int> stock, string unavailable = "$msg_noprocessableitems") =>
            item ? verb + " " + Stocked(item, stock(item)) : Local(unavailable);
        private static ItemDrop Prefab(ItemDrop.ItemData item) => item?.m_dropPrefab ? item.m_dropPrefab.GetComponent<ItemDrop>() : null;

        // Replace only the automatic Use line. Manual item-selection, configuration and timer lines stay in place.
        private static string Actions(string text, string single, params string[] extra)
        {
            var lines = Local(text).Split('\n').ToList();
            string key = "[" + Tags.Replace(Local("$KEY_Use"), "") + "]";
            int index = lines.FindIndex(line => Tags.Replace(line, "").TrimStart().StartsWith(key, StringComparison.Ordinal));
            if (index < 0) { index = Math.Min(1, lines.Count); lines.Insert(index, Use(single)); }
            else lines[index] = Use(single);
            lines.InsertRange(index + 1, extra.Where(line => !string.IsNullOrEmpty(line)));
            return string.Join("\n", lines);
        }

        private static ItemDrop OrdinaryCooking(CookingStation station, Supplies supplies)
        {
            foreach (var recipe in station.m_conversion)
                if (recipe.m_from && supplies.Bag.GetItem(recipe.m_from.m_itemData.m_shared.m_name) is ItemDrop.ItemData item) return Prefab(item);
            foreach (var recipe in station.m_conversion)
                if (recipe.m_from && BulkProcessing.CanPull(station, recipe.m_from.name) && supplies.ChestFirst(station, recipe.m_from)) return recipe.m_from;
            return null;
        }
        private static string CookingAction(CookingStation station)
        {
            string prefab = Utils.GetPrefabName(station.gameObject);
            return prefab == "piece_oven" ? "Add food:" : prefab == "piece_FrostFoundry" ? Local(station.m_addItemTooltip).TrimEnd(':') + ":" : "Add food:";
        }
        private static string CookingSingle(CookingStation station, Supplies supplies)
        {
            if (BulkCooking.FinishedCount(station) > 0) return Local(station.m_fullyCookedTooltip);
            if (station.m_requireFire && !FireLit(station)) return Local("$msg_needfire");
            if (BulkCooking.EmptyCount(station) == 0) return Local("$msg_nocookroom");
            return Selected(CookingAction(station), OrdinaryCooking(station, supplies), item => supplies.Stock(station, station, item), station.m_noCookableItemsMessage);
        }
        private static string CookingBulk(CookingStation station, Supplies supplies, float radius)
        {
            if (BulkCooking.Busy) return "Cooking action in progress";
            if (BulkCooking.FinishedCount(station) > 0) return "Collect finished items nearby";
            var supply = BulkCooking.FirstSupply(station, supplies.Player, radius);
            if (supply != null)
            {
                string action = CookingAction(station);
                action = action.EndsWith(" item:", StringComparison.Ordinal) ? action.Replace(" item:", " nearby:").TrimEnd(':') : action.TrimEnd(':') + " nearby";
                return action + ": " + Stocked(Prefab(supply.Item), BulkCooking.Stock(station, supplies.Player, supply.Item.m_dropPrefab.name, radius));
            }
            var available = BulkCooking.Nearby(station, radius).Where(candidate => BulkCooking.EmptyCount(candidate) > 0 && (!candidate.m_requireFire || FireLit(candidate))).ToArray();
            if (available.Length == 0) return "No eligible cooking space nearby";
            return available.Any(candidate => OrdinaryCooking(candidate, supplies)) ? "Only protected ingredients available" : Local(station.m_noCookableItemsMessage);
        }
        private static string FoodText(CookingStation station, string text, Supplies supplies)
        {
            bool bulk = BulkCooking.Supported(station) && OwnModifier(out _);
            OwnModifier(out float radius);
            return Actions(text, CookingSingle(station, supplies), bulk ? Bulk(CookingBulk(station, supplies, radius)) : null);
        }

        private static string WithCookingTimers(CookingStation station, string text)
        {
            if (ShowCookingTimers?.Invoke() != true) return text;
            string timers = CookingTimers(station);
            return string.IsNullOrEmpty(timers) || text.Contains(timers) ? text : text + "\n" + timers;
        }

        private static ItemDrop OrdinaryFuel(Component station, ItemDrop fuel, Supplies supplies, bool cooking)
        {
            if (!fuel) return null;
            if (supplies.BagCount(fuel) > 0) return fuel;
            return BulkProcessing.CanPull(station, fuel.name) ? supplies.ChestFirst(station, fuel, cooking) : null;
        }
        private static string MachineBulk(Component station, bool fuel, Supplies supplies, float radius)
        {
            if (BulkProcessing.Busy) return "Station action in progress";
            var nearby = BulkProcessing.Nearby(station, radius).Where(candidate => BulkProcessing.Room(candidate, fuel) > 0).ToArray();
            if (nearby.Length == 0) return fuel ? "Nearby fuel is full" : "No input space nearby";
            var ingredient = BulkProcessing.SelectIngredient(station, supplies.Player, fuel);
            if (ingredient && nearby.Any(candidate => BulkProcessing.FindSupply(candidate, supplies.Player, ingredient, fuel) != null))
                return (fuel ? "Refuel nearby with " : OreAction((Smelter)station).Replace(" item:", " nearby:") + " ") +
                    Stocked(ingredient, BulkProcessing.Stock(station, supplies.Player, ingredient, fuel, radius));
            bool ordinary = nearby.Any(candidate => fuel ? OrdinaryFuel(candidate, BulkProcessing.Fuel(candidate), supplies, candidate is CookingStation) : OrdinaryOre((Smelter)candidate, supplies));
            return ordinary ? "Only protected ingredients available" : fuel ? "No eligible fuel" : OreUnavailable((Smelter)station, supplies);
        }
        private static string OreAction(Smelter station)
        {
            string prefab = Utils.GetPrefabName(station.gameObject);
            return prefab == "smelter" || prefab == "blastfurnace" ? "Smelt item:" : prefab == "charcoal_kiln" || prefab == "kiln engine" ? "Burn item:" : "Process item:";
        }
        private static ItemDrop OrdinaryOre(Smelter station, Supplies supplies)
        {
            var recipes = station.m_conversion.Where(recipe => recipe.m_from && OreAllowed(station, recipe.m_from.name)).ToArray();
            bool native = !supplies.Crafty || recipes.Any(recipe => supplies.BagCount(recipe.m_from) > 0 && BulkProcessing.CanPull(station, recipe.m_from.name));
            if (native) return recipes.Select(recipe => supplies.Bag.GetItem(recipe.m_from.m_itemData.m_shared.m_name)).Where(item => item != null).Select(Prefab).FirstOrDefault();
            foreach (var recipe in recipes)
                if (BulkProcessing.CanPull(station, recipe.m_from.name) && supplies.ChestFirst(supplies.Player, recipe.m_from)) return recipe.m_from;
            return null;
        }
        private static string OreUnavailable(Smelter station, Supplies supplies)
        {
            foreach (var recipe in station.m_conversion.Where(recipe => recipe.m_from))
            {
                string reason = SmeltingProgression.Locked(station, recipe.m_from.name);
                if (reason != null && (supplies.BagCount(recipe.m_from) > 0 || supplies.ChestFirst(supplies.Player, recipe.m_from))) return reason;
            }
            return Local("$msg_noprocessableitems");
        }
        private static string OreFill(Smelter station, Supplies supplies)
        {
            int room = BulkProcessing.Room(station, false);
            if (room == 0) return Local("$msg_itsfull");
            var parts = new List<(ItemDrop, int, int)>();
            var recipes = station.m_conversion.Where(recipe => recipe.m_from && OreAllowed(station, recipe.m_from.name))
                .OrderByDescending(recipe => supplies.BagCount(recipe.m_from) > 0).ToArray();
            foreach (var recipe in recipes)
            {
                var item = recipe.m_from;
                if (!BulkProcessing.CanPull(station, item.name)) continue;
                int stock = supplies.Stock(supplies.Player, station, item);
                int amount = supplies.Take(supplies.Bag, item, Math.Min(room, supplies.BagCount(item))); room -= amount;
                foreach (var chest in supplies.Chests(supplies.Player))
                {
                    int take = Math.Min(room, supplies.ChestCount(chest, item)); amount += supplies.Take(chest, item, take); room -= take;
                    if (room == 0) break;
                }
                if (amount > 0) parts.Add((item, amount, stock));
                if (room == 0) break;
            }
            return parts.Count > 0 ? "Fill: " + Parts(parts) : OreUnavailable(station, supplies);
        }
        private static string FuelFill(Component station, ItemDrop fuel, Supplies supplies, int room)
        {
            if (room <= 0) return "Fuel full";
            int stock = supplies.Stock(station, station, fuel), amount = supplies.Fill(station, fuel, room, true);
            return amount > 0 ? "Add fuel: " + amount + " x " + Stocked(fuel, stock) : "No eligible fuel";
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(CookingStation), nameof(CookingStation.GetHoverText))]
        private static void CookingHover(CookingStation __instance, ref string __result)
        {
            if (!Valid(__instance) || __instance.m_addFoodSwitch || Denied(__result)) return;
            __result = FoodText(__instance, __result, new Supplies());
        }
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(Switch), nameof(Switch.GetHoverText))]
        private static void SwitchHover(Switch __instance, ref string __result)
        {
            if (!Player.m_localPlayer || Denied(__result)) return;
            var supplies = new Supplies();
            var cooking = __instance.GetComponentInParent<CookingStation>();
            if (cooking && Valid(cooking))
            {
                if (__instance == cooking.m_addFoodSwitch)
                {
                    __result = WithCookingTimers(cooking, FoodText(cooking, __result, supplies));
                }
                else if (__instance == cooking.m_addFuelSwitch)
                {
                    bool own = BulkProcessing.Supported(cooking) && OwnModifier(out _); OwnModifier(out float radius);
                    string single = BulkProcessing.Room(cooking, true) == 0 ? "Fuel full" : Selected("Add", OrdinaryFuel(cooking, cooking.m_fuelItem, supplies, true),
                        item => supplies.Stock(cooking, cooking, item), "$msg_outof " + cooking.m_fuelItem.m_itemData.m_shared.m_name);
                    __result = Actions(__result, single, own ? Bulk(MachineBulk(cooking, true, supplies, radius)) : null);
                    __result = WithCookingTimers(cooking, __result + "\n" + CookingSingle(cooking, supplies));
                }
                return;
            }
            var machine = __instance.GetComponentInParent<Smelter>();
            if (machine && Valid(machine) && (__instance == machine.m_addOreSwitch || __instance == machine.m_addWoodSwitch && !AshlandsHotTubs.Heated(machine)))
            {
                bool fuel = __instance == machine.m_addWoodSwitch;
                bool own = BulkProcessing.Supported(machine) && OwnModifier(out _); OwnModifier(out float radius);
                string single;
                if (BulkProcessing.Room(machine, fuel) == 0) single = fuel ? "Fuel full" : Local("$msg_itsfull");
                else if (fuel) single = Selected("Add", OrdinaryFuel(machine, machine.m_fuelItem, supplies, false), item => supplies.Stock(machine, machine, item),
                    "$msg_outof " + machine.m_fuelItem.m_itemData.m_shared.m_name);
                else single = Selected(OreAction(machine), OrdinaryOre(machine, supplies), item => supplies.Stock(supplies.Player, machine, item), OreUnavailable(machine, supplies));
                string nearby = own ? Bulk(MachineBulk(machine, fuel, supplies, radius)) : null;
                supplies.Reset();
                string fill = ShowFill(own) ? FillHint(fuel ? FuelFill(machine, machine.m_fuelItem, supplies, BulkProcessing.Room(machine, true)) : OreFill(machine, supplies)) : null;
                __result = Actions(__result, single, nearby, fill);
                return;
            }
            var shield = __instance.GetComponentInParent<ShieldGenerator>();
            if (shield && Valid(shield) && __instance == shield.m_addFuelSwitch && shield.m_fuelItems.Count > 0)
            {
                int room = Math.Max(0, Mathf.FloorToInt(shield.m_maxFuel - shield.GetComponent<ZNetView>().GetZDO().GetFloat(ZDOVars.s_fuel)));
                ItemDrop item = shield.m_fuelItems.FirstOrDefault(value => supplies.BagCount(value) > 0);
                if (!item) item = shield.m_fuelItems.FirstOrDefault(value => BulkProcessing.CanPull(shield, value.name) && supplies.ChestFirst(shield, value));
                string single = room == 0 ? "Fuel full" : Selected("Add fuel:", item, value => supplies.Stock(shield, shield, value));
                var parts = new List<(ItemDrop, int, int)>();
                if (ShowFill(false)) foreach (var value in shield.m_fuelItems)
                {
                    int stock = supplies.Stock(shield, shield, value), amount = supplies.Fill(shield, value, room, true); room -= amount;
                    if (amount > 0) parts.Add((value, amount, stock));
                    if (room == 0) break;
                }
                __result = Actions(__result, single, ShowFill(false) ? FillHint(parts.Count > 0 ? "Add fuel: " + Parts(parts) : room == 0 ? "Fuel full" : "No eligible fuel") : null);
            }
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(Fireplace), nameof(Fireplace.GetHoverText))]
        private static void FireHover(Fireplace __instance, ref string __result)
        {
            if (!Valid(__instance) || __instance.m_infiniteFuel || !__instance.m_canRefill || !__instance.m_fuelItem || Denied(__result)) return;
            bool toggle = __instance.m_canTurnOff && __instance.GetComponent<ZNetView>().GetZDO().GetFloat(ZDOVars.s_fuel) > 0f;
            if (KeepLitFires.IsKeptLit(__instance))
            {
                if (!toggle)
                {
                    string use = "[" + Tags.Replace(Local("$KEY_Use"), "") + "]", manual = "[" + Tags.Replace(Local("$KEY_HotbarUse"), "") + "]";
                    __result = string.Join("\n", Local(__result).Split('\n').Where(line => { string plain = Tags.Replace(line, "").TrimStart(); return !plain.StartsWith(use, StringComparison.Ordinal) && !plain.StartsWith(manual, StringComparison.Ordinal); }));
                }
                __result += "\nFuel maintained automatically"; return;
            }
            var supplies = new Supplies();
            int room = Math.Max(0, Mathf.FloorToInt(__instance.m_maxFuel - Mathf.Ceil(__instance.GetComponent<ZNetView>().GetZDO().GetFloat(ZDOVars.s_fuel))));
            string fill = ShowFill(false, true) ? FillHint(FuelFill(__instance, __instance.m_fuelItem, supplies, room), true) : null;
            supplies.Reset();
            if (toggle) { if (fill != null) __result += "\n" + fill; return; }
            string single = room == 0 ? "Fuel full" : Selected("Add fuel:", OrdinaryFuel(__instance, __instance.m_fuelItem, supplies, false),
                item => supplies.Stock(__instance, __instance, item), "$msg_outof " + __instance.m_fuelItem.m_itemData.m_shared.m_name);
            __result = Actions(__result, single, fill);
        }

        private static ItemDrop NativeAmmo(Turret turret, Supplies supplies)
        {
            var item = AmmoCount(turret) > 0 ? supplies.Bag.GetAmmoItem(turret.m_ammoType, AmmoType(turret)) : supplies.Bag.GetAmmoItem(turret.m_ammoType);
            return item?.m_dropPrefab && AmmoAllowed(turret, item.m_dropPrefab.name) ? Prefab(item) : null;
        }
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(Turret), nameof(Turret.GetHoverText))]
        private static void TurretHover(Turret __instance, ref string __result)
        {
            if (!Valid(__instance) || __instance.m_maxAmmo <= 0 || !__instance.m_targetEnemies || Denied(__result)) return;
            var supplies = new Supplies(); int room = Math.Max(0, __instance.m_maxAmmo - AmmoCount(__instance));
            var ammo = NativeAmmo(__instance, supplies);
            var current = ZNetScene.instance.GetPrefab(AmmoType(__instance))?.GetComponent<ItemDrop>();
            if (!ammo && current && BulkProcessing.CanPull(__instance, current.name)) ammo = supplies.ChestFirst(__instance, current);
            // Storage supplies only the ammunition already loaded; other carried ammunition counts what the bag holds.
            int Stock(ItemDrop item) => current && item.name == current.name ? supplies.Stock(__instance, __instance, item) : supplies.Bag.CountItems(item.m_itemData.m_shared.m_name);
            string single = room == 0 ? Local("$msg_itsfull") : Selected("Add ammunition:", ammo, Stock, "$msg_noturretammo");
            string fill = null;
            if (ShowFill(false, true))
            {
                int stock = current ? Stock(current) : 0, amount = current ? supplies.Fill(__instance, current, room, true) : 0;
                fill = FillHint(room == 0 ? Local("$msg_itsfull") : amount > 0 ? "Add ammunition: " + amount + " x " + Stocked(current, stock) :
                    Selected("Add ammunition:", NativeAmmo(__instance, supplies), Stock, "$msg_noturretammo"), true);
            }
            __result = Actions(__result, single, fill);
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(ItemStand), nameof(ItemStand.GetHoverText))]
        private static void StandHover(ItemStand __instance, ref string __result)
        {
            if (!Player.m_localPlayer || !__instance.m_autoAttach || __instance.m_supportedItems.Count != 1 || __instance.HaveAttachment() || Denied(__result)) return;
            var item = __instance.m_supportedItems[0];
            // The holder takes only carried copies; storage never supplies it.
            int carried = Player.m_localPlayer.GetInventory().CountItems(item.m_itemData.m_shared.m_name);
            __result = Actions(__result, carried > 0 ? "Attach item: " + Stocked(item, carried) : "Missing " + Name(item));
        }
    }

    // FedoHud writes its food-switch timers into a field the native switch callback does not read. Append the same projected lines at the final switch result.
    internal static class StationInputFedo
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.FedoHud, out var plugin)) return;
            var type = plugin.Instance.GetType().Assembly.GetType("FedoHud.CookingStationTooltip", true);
            var show = AccessTools.Property(plugin.Instance.GetType(), "ShowCookingTooltip") ?? throw new MissingMemberException("FedoHud's cooking preference changed.");
            var lines = AccessTools.MethodDelegate<Func<CookingStation, string>>(AccessTools.Method(type, "BuildLines"));
            IntegrationChanges.Defer(() => { StationInputPreviews.CookingTimers = lines; StationInputPreviews.ShowCookingTimers = () => (bool)show.GetValue(plugin.Instance); },
                () => { StationInputPreviews.CookingTimers = null; StationInputPreviews.ShowCookingTimers = null; });
            harmony.Patch(AccessTools.Method(type, "AppendToSwitch"), prefix: new HarmonyMethod(typeof(StationInputFedo), nameof(UseFinalSwitch)));
            harmony.Patch(AccessTools.Method(type, "BuildLines"), transpiler: new HarmonyMethod(typeof(StationInputFedo), nameof(LocalizeTimerItems)));
        }
        private static bool UseFinalSwitch() => !OptionalIntegrations.Enabled(typeof(StationInputPreviews));

        private static IEnumerable<CodeInstruction> LocalizeTimerItems(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var names = code.Where(instruction => instruction.operand is MethodInfo method && method.Name == "LocalizeOrRaw" &&
                method.DeclaringType.FullName == "FedoHud.GameLocalization").ToArray();
            if (names.Length != 1) throw new InvalidOperationException("FedoHud's cooking item-name lookup changed.");
            names[0].operand = AccessTools.Method(typeof(StationInputFedo), nameof(ItemName));
            return code;
        }

        private static string ItemName(string prefabName)
        {
            var prefab = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            var item = prefab ? prefab.GetComponent<ItemDrop>() : null;
            return Localization.instance.Localize(item ? item.m_itemData.m_shared.m_name : prefabName);
        }
    }
}
