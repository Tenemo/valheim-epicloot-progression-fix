using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Ctrl + left click on a piece in a build tool's menu pulls from the chests AzuCraftyBoxes builds from what the
    // inventory lacks for one more build of that piece, or as much as they hold, and leaves the menu open. A piece that
    // costs nothing is selected as usual. AzuCraftyBoxes chooses the chests and items as when it builds from them: its
    // range, chest switch, exclusions, Leave One Item and pulling mode. Left Ctrl is also crouch, but the game cancels
    // crouching while a build tool is out, so the click does not crouch.
    internal static class BuildMaterialPull
    {
        private static ConfigEntry<float> range;
        private static Func<bool> pullingPrevented;
        private static Func<string, string, bool> canBePulled;
        private static Func<int, int> leaveOne;
        private static MethodInfo nearbyContainers;
        private static MethodInfo containerInventory;
        private static MethodInfo containerPrefab;
        private static MethodInfo saveContainer;
        private static Type vanillaContainer;
        private static FieldInfo vanillaChest;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var api = assembly.GetType("AzuCraftyBoxes.API");
            var container = assembly.GetType("AzuCraftyBoxes.IContainers.IContainer");
            var functions = assembly.GetType("AzuCraftyBoxes.Util.Functions.MiscFunctions");
            var boxes = assembly.GetType("AzuCraftyBoxes.Util.Functions.Boxes");
            var prevented = functions == null ? null : AccessTools.Method(functions, "ShouldPrevent", Type.EmptyTypes);
            var pullable = api == null ? null : AccessTools.Method(api, "CanItemBePulled", new[] { typeof(string), typeof(string) });
            var decrement = boxes == null ? null : AccessTools.Method(boxes, "CheckAndDecrement", new[] { typeof(int) });
            nearbyContainers = api == null ? null : AccessTools.Method(api, "GetNearbyContainers")?.MakeGenericMethod(typeof(Player));
            containerInventory = container == null ? null : AccessTools.Method(container, "GetInventory", Type.EmptyTypes);
            containerPrefab = container == null ? null : AccessTools.Method(container, "GetPrefabName", Type.EmptyTypes);
            saveContainer = container == null ? null : AccessTools.Method(container, "Save", Type.EmptyTypes);
            vanillaContainer = assembly.GetType("AzuCraftyBoxes.IContainers.VanillaContainer");
            vanillaChest = vanillaContainer == null ? null :
                AccessTools.GetDeclaredFields(vanillaContainer).SingleOrDefault(field => field.FieldType == typeof(Container));
            if (!plugin.Instance.Config.TryGetEntry("2 - CraftyBoxes", "Container Range", out range) || prevented == null ||
                pullable == null || decrement == null || nearbyContainers == null || containerInventory == null ||
                containerPrefab == null || saveContainer == null || vanillaChest == null)
                throw new InvalidOperationException("AzuCraftyBoxes' chest access moved.");
            pullingPrevented = AccessTools.MethodDelegate<Func<bool>>(prevented);
            canBePulled = AccessTools.MethodDelegate<Func<string, string, bool>>(pullable);
            leaveOne = AccessTools.MethodDelegate<Func<int, int>>(decrement);
            harmony.Patch(AccessTools.Method(typeof(BuildUi), nameof(BuildUi.OnSelectPiece)),
                prefix: new HarmonyMethod(typeof(BuildMaterialPull), nameof(PullInsteadOfSelecting)));
            log.LogInfo("Ctrl + left click in the build menu pulls a piece's materials from AzuCraftyBoxes' chests.");
        }

        private static bool PullInsteadOfSelecting(Piece piece)
        {
            if (!ZInput.GetKey(KeyCode.LeftControl) && !ZInput.GetKey(KeyCode.RightControl)) return true;
            var player = Player.m_localPlayer;
            // With AzuCraftyBoxes' pulling off, the click selects the piece as it would without the mod.
            if (player == null || piece == null || pullingPrevented()) return true;
            var costs = Costs(piece);
            if (costs.Count == 0) return true;
            Pull(player, costs);
            return false;
        }

        // One build's materials, each item once: the game takes every entry with an item and an amount.
        private static List<(ItemDrop item, int amount)> Costs(Piece piece)
        {
            var costs = new List<(ItemDrop item, int amount)>();
            foreach (var requirement in piece.m_resources)
            {
                if (requirement.m_resItem == null || requirement.m_amount <= 0) continue;
                string name = requirement.m_resItem.m_itemData.m_shared.m_name;
                int index = costs.FindIndex(cost => cost.item.m_itemData.m_shared.m_name == name);
                if (index < 0) costs.Add((requirement.m_resItem, requirement.m_amount));
                else costs[index] = (costs[index].item, costs[index].amount + requirement.m_amount);
            }
            return costs;
        }

        private static void Pull(Player player, List<(ItemDrop item, int amount)> costs)
        {
            var inventory = player.GetInventory();
            // The builds the inventory already covers, counted as the game counts them for building.
            int builds = costs.Min(cost => inventory.CountItems(cost.item.m_itemData.m_shared.m_name) / cost.amount);
            var containers = Chests(player);
            var missing = new List<string>();
            bool noRoom = false;
            foreach (var (item, amount) in costs)
            {
                string name = item.m_itemData.m_shared.m_name;
                int wanted = (builds + 1) * amount - inventory.CountItems(name);
                if (wanted <= 0) continue;
                int pulled = 0;
                bool full = false;
                foreach (var container in containers)
                {
                    if (pulled >= wanted || full) break;
                    pulled += PullFrom(container, item, wanted - pulled, inventory, ref full);
                }
                if (pulled > 0) player.ShowPickupMessage(item.m_itemData, pulled);
                if (pulled < wanted) missing.Add($"{wanted - pulled} {name}");
                noRoom |= full;
            }
            if (noRoom) player.Message(MessageHud.MessageType.Center, "$msg_noroom");
            else if (missing.Count > 0) player.Message(MessageHud.MessageType.Center, "Not in nearby chests: " + string.Join(", ", missing));
        }

        // What AzuCraftyBoxes may take of a material from the chests in range, counted as it counts them for a craft or a
        // build; nothing while its pulling is off. AAA_Crafting's crafting Max uses this too.
        internal static int InChests(Player player, ItemDrop material)
        {
            if (pullingPrevented()) return 0;
            int count = 0;
            foreach (var container in Chests(player))
                count += Pullable(container, material);
            return count;
        }

        // The chests AzuCraftyBoxes builds from, as a copy: it refills its list in place when it looks for chests again.
        // For a quarter second after each search it hands out the same list, which can still hold a chest destroyed since,
        // broken or unloaded with its zone. That chest is left out, as AzuCraftyBoxes' next search leaves it out: its
        // leftover inventory is no longer in the world, and the chest can no longer be read or saved.
        private static List<object> Chests(Player player) =>
            ((IEnumerable)nearbyContainers.Invoke(null, new object[] { player, range.Value })).Cast<object>()
                .Where(container => !vanillaContainer.IsInstanceOfType(container) || (Container)vanillaChest.GetValue(container) != null)
                .ToList();

        // Nothing from a chest its exclusions close to the material, and one less than the chest holds with Leave One Item.
        private static int Pullable(object container, ItemDrop material)
        {
            var chest = (Inventory)containerInventory.Invoke(container, null);
            if (chest == null || !canBePulled((string)containerPrefab.Invoke(container, null), material.name)) return 0;
            return leaveOne(chest.CountItems(material.m_itemData.m_shared.m_name));
        }

        // Moves stacks of the material from one chest into the inventory as copies, taking them out of the chest as
        // AzuCraftyBoxes does when it builds: an emptied stack by its index and the rest by its size, which
        // MultiUserChest lets through, then saving the chest. The chest gives up only what the inventory took.
        private static int PullFrom(object container, ItemDrop material, int wanted, Inventory inventory, ref bool full)
        {
            int allowed = Math.Min(wanted, Pullable(container, material));
            if (allowed <= 0) return 0;
            var chest = (Inventory)containerInventory.Invoke(container, null);
            string name = material.m_itemData.m_shared.m_name;
            int pulled = 0;
            var items = chest.GetAllItems();
            for (int i = 0; i < items.Count && pulled < allowed; i++)
            {
                var item = items[i];
                if (item.m_shared.m_name != name || item.m_worldLevel < Game.m_worldLevel) continue;
                int room = inventory.FindFreeStackSpace(name, item.m_worldLevel) + inventory.GetEmptySlots() * item.m_shared.m_maxStackSize;
                int take = Math.Min(item.m_stack, Math.Min(allowed - pulled, room));
                int added = 0;
                if (take > 0)
                {
                    var copy = item.Clone();
                    copy.m_stack = take;
                    int before = inventory.CountItems(name, -1, false);
                    inventory.AddItem(copy);
                    added = inventory.CountItems(name, -1, false) - before;
                }
                if (added > 0)
                {
                    if (added >= item.m_stack)
                    {
                        chest.RemoveItem(i);
                        i--;
                    }
                    else item.m_stack -= added;
                    pulled += added;
                }
                if (added < take || take <= 0)
                {
                    full = true;
                    break;
                }
            }
            if (pulled > 0) saveContainer.Invoke(container, null);
            return pulled;
        }
    }
}
