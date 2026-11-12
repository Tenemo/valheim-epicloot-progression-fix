using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The black metal wardrobe stores 5 columns by 10 rows, which the chest-sized container window shows as four
    // visible rows and a scrollbar. Its window instead shows the same 50 slots as 10 columns by 5 rows, in reading order,
    // and grows by two columns and one row around its top edge. The inventory, its saved cell coordinates and every click,
    // drag and transfer stay native: only element positions change, and controller movement follows the visible grid.
    // Other containers, and EpicLoot's views in the same window, keep the native layout.
    internal static class WardrobeLayout
    {
        private const string Prefab = "piece_chest_warderobe";
        private const int Columns = 10, Rows = 5;
        private const float Margin = 8f;
        private static readonly AccessTools.FieldRef<InventoryGui, InventoryGrid> ContainerGrid = AccessTools.FieldRefAccess<InventoryGui, InventoryGrid>("m_containerGrid");
        private static readonly AccessTools.FieldRef<InventoryGui, InventoryGrid> PlayerGrid = AccessTools.FieldRefAccess<InventoryGui, InventoryGrid>("m_playerGrid");
        private static readonly AccessTools.FieldRef<InventoryGui, Container> CurrentContainer = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
        private static readonly AccessTools.FieldRef<InventoryGui, int> ActiveGroup = AccessTools.FieldRefAccess<InventoryGui, int>("m_activeGroup");
        private static readonly Action<InventoryGui, int, bool> SetActiveGroup = AccessTools.MethodDelegate<Action<InventoryGui, int, bool>>(
            AccessTools.Method(typeof(InventoryGui), "SetActiveGroup", new[] { typeof(int), typeof(bool) }));
        private static readonly AccessTools.FieldRef<InventoryGrid, Inventory> GridInventory = AccessTools.FieldRefAccess<InventoryGrid, Inventory>("m_inventory");
        private static readonly AccessTools.FieldRef<InventoryGrid, List<InventoryElement>> Elements = AccessTools.FieldRefAccess<InventoryGrid, List<InventoryElement>>("m_elements");
        private static readonly AccessTools.FieldRef<InventoryGrid, Vector2i> Selected = AccessTools.FieldRefAccess<InventoryGrid, Vector2i>("m_selected");
        private static readonly AccessTools.FieldRef<InventoryGrid, bool> JumpToNext = AccessTools.FieldRefAccess<InventoryGrid, bool>("jumpToNextContainer");
        // The grid currently shown as 10 by 5, the elements it was laid out with, and the window's original geometry.
        private static InventoryGrid shaped;
        private static InventoryElement laidOut;
        private static RectTransform panel;
        private static Vector2 panelSize, panelPosition, growth;
        private static readonly Dictionary<RectTransform, Vector2> Moved = new Dictionary<RectTransform, Vector2>();

        private static bool IsWardrobe(InventoryGui gui, Inventory inventory)
        {
            var container = CurrentContainer(gui);
            return container && inventory != null && container.GetInventory() == inventory && inventory.GetWidth() * inventory.GetHeight() == Columns * Rows &&
                Utils.GetPrefabName(container.gameObject) == Prefab;
        }

        [HarmonyPatch(typeof(InventoryGrid), "UpdateGui")]
        private static class Layout
        {
            private static void Postfix(InventoryGrid __instance)
            {
                var gui = InventoryGui.instance;
                if (!gui || __instance != ContainerGrid(gui)) return;
                if (!IsWardrobe(gui, GridInventory(__instance)))
                {
                    if (shaped == __instance) Restore();
                    return;
                }
                Grow(gui, __instance);
                var elements = Elements(__instance);
                if (elements.Count == Columns * Rows && elements[0] != laidOut)
                {
                    float space = __instance.m_elementSpace;
                    var origin = new Vector2(((RectTransform)__instance.transform).rect.width / 2f - Columns * space / 2f, 0f);
                    for (int index = 0; index < elements.Count; index++)
                        ((RectTransform)elements[index].transform).anchoredPosition = origin + new Vector2(index % Columns * space, -(index / Columns) * space);
                    laidOut = elements[0];
                }
                var root = __instance.m_gridRoot;
                if (!Mathf.Approximately(root.rect.height, Rows * __instance.m_elementSpace))
                    root.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Rows * __instance.m_elementSpace);
            }
        }

        // The window keeps its top-left corner, under the bag, while it grows by the missing columns and rows; buttons
        // around it keep their distance from the edge they sit beside.
        private static void Grow(InventoryGui gui, InventoryGrid grid)
        {
            if (shaped != grid)
            {
                Restore();
                panel = gui.m_container;
                panelSize = panel.sizeDelta;
                panelPosition = panel.anchoredPosition;
                var area = ((RectTransform)grid.transform).rect.size;
                float space = grid.m_elementSpace;
                growth = new Vector2(Mathf.Max(0f, Columns - Mathf.Floor(area.x / space)), Mathf.Max(0f, Rows - Mathf.Floor(area.y / space))) * space;
                shaped = grid;
                laidOut = null;
            }
            var size = panelSize + growth;
            if (panel.sizeDelta != size) panel.sizeDelta = size;
            // At large UI scales the extra row could leave the screen, so the window then shrinks about its top edge.
            // Its top stays where the unscaled window's top is, so the measured top does not depend on this scale.
            var corners = new Vector3[4];
            panel.GetWorldCorners(corners);
            float canvas = panel.parent.lossyScale.y, height = panel.rect.height;
            float fit = Mathf.Clamp((corners[1].y - Margin * canvas) / (height * canvas), .5f, 1f);
            if (!Mathf.Approximately(panel.localScale.x, fit)) panel.localScale = new Vector3(fit, fit, 1f);
            float above = panel.pivot.y - 1f;
            var position = panelPosition + new Vector2(0f, above * growth.y - above * height * (1f - fit));
            if (panel.anchoredPosition != position) panel.anchoredPosition = position;
            foreach (RectTransform child in panel)
            {
                if (child.anchorMin != new Vector2(.5f, .5f) || child.anchorMax != child.anchorMin) continue;
                if (!Moved.TryGetValue(child, out var original)) Moved[child] = original = child.anchoredPosition;
                var target = original + new Vector2(Math.Sign(original.x) * growth.x / 2f, growth.y / 2f);
                if (child.anchoredPosition != target) child.anchoredPosition = target;
            }
        }

        private static void Restore()
        {
            if (panel)
            {
                panel.sizeDelta = panelSize;
                panel.anchoredPosition = panelPosition;
                panel.localScale = Vector3.one;
            }
            foreach (var pair in Moved) if (pair.Key) pair.Key.anchoredPosition = pair.Value;
            Moved.Clear();
            // The native layout rebuilds its elements the next time the grid's size changes.
            if (shaped) AccessTools.FieldRefAccess<InventoryGrid, int>("m_width")(shaped) = -1;
            shaped = null;
            laidOut = null;
            panel = null;
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
        private static class Close
        {
            private static void Postfix()
            {
                if (shaped) Restore();
            }
        }

        private static Vector2i Shown(Vector2i cell, int width) => new Vector2i((cell.y * width + cell.x) % Columns, (cell.y * width + cell.x) / Columns);

        private static Vector2i Stored(Vector2i shown, int width) => new Vector2i((shown.y * Columns + shown.x) % width, (shown.y * Columns + shown.x) / width);

        // Native controller movement, measured on the visible 10 by 5 grid instead of the stored 5 by 10 one.
        [HarmonyPatch(typeof(InventoryGrid), "UpdateGamepad")]
        private static class Controller
        {
            private static bool Prefix(InventoryGrid __instance)
            {
                if (__instance != shaped || !__instance.m_uiGroup.IsActive || Console.IsVisible() || ZInput.IsTouchActive() || !ZInput.IsExclusiveGamepadActive())
                    return true;
                var inventory = GridInventory(__instance);
                int width = inventory.GetWidth();
                ref var selected = ref Selected(__instance);
                ref bool jump = ref JumpToNext(__instance);
                var before = selected;
                var shown = Shown(selected, width);
                if (ZInput.GetButtonDown("JoyDPadLeft") || ZInput.GetButtonDown("JoyLStickLeft")) shown.x = Math.Max(0, shown.x - 1);
                if (ZInput.GetButtonDown("JoyDPadRight") || ZInput.GetButtonDown("JoyLStickRight")) shown.x = Math.Min(Columns - 1, shown.x + 1);
                if (ZInput.GetButtonDown("JoyDPadUp") || ZInput.GetButtonDown("JoyLStickUp"))
                {
                    if (shown.y == 0)
                    {
                        if (!jump) return false;
                        __instance.OnMoveToUpperInventoryGrid?.Invoke(shown);
                    }
                    else { shown.y--; jump = false; }
                }
                if (!ZInput.GetButton("JoyDPadUp") && !ZInput.GetButton("JoyLStickUp") && shown.y - 1 <= 0) jump = true;
                if (ZInput.GetButtonDown("JoyDPadDown") || ZInput.GetButtonDown("JoyLStickDown"))
                {
                    if (shown.y == Rows - 1)
                    {
                        if (!jump) return false;
                        __instance.OnMoveToLowerInventoryGrid?.Invoke(shown);
                    }
                    else { shown.y++; jump = false; }
                }
                selected = Stored(shown, width);
                if (selected != before)
                {
                    var element = __instance.GetGamepadSelectedElement();
                    element.GetComponent<UnityEngine.UI.Selectable>().Select();
                    __instance.m_ensureVisible?.CenterOnItem(element);
                }
                if (!ZInput.GetButton("JoyDPadDown") && !ZInput.GetButton("JoyLStickDown") && shown.y + 1 >= Rows - 1) jump = true;
                if (ZInput.GetButtonDown("JoyButtonA"))
                {
                    var modifier = ZInput.GetButton("JoyRTrigger") ? InventoryGrid.Modifier.Drop : ZInput.GetButton("JoyLTrigger") ? InventoryGrid.Modifier.Split : InventoryGrid.Modifier.Select;
                    __instance.m_onSelected(__instance, __instance.GetGamepadSelectedItem(), selected, modifier);
                }
                if (ZInput.GetButtonDown("JoyButtonX"))
                {
                    var item = __instance.GetGamepadSelectedItem();
                    if (ZInput.GetButton("JoyLTrigger")) __instance.m_onSelected(__instance, item, selected, InventoryGrid.Modifier.Move);
                    else __instance.m_onRightClick(__instance, item, selected);
                }
                return false;
            }
        }

        // Moving between the bag and the wardrobe keeps the column the player sees: the bag's columns sit over the
        // wardrobe's first ones.
        [HarmonyPatch(typeof(InventoryGui), "MoveToLowerInventoryGrid")]
        private static class IntoWardrobe
        {
            private static bool Prefix(InventoryGui __instance, Vector2i previousGridPosition)
            {
                var grid = ContainerGrid(__instance);
                if (grid != shaped || shaped == null) return true;
                if (!__instance.m_inventoryGroup.IsActive || !__instance.IsContainerOpen()) return false;
                                grid.SetGamepadSelection(Stored(new Vector2i(Mathf.Clamp(previousGridPosition.x, 0, Columns - 1), 0), GridInventory(grid).GetWidth()));
                SetActiveGroup(__instance, ActiveGroup(__instance) - 1, true);
                return false;
            }
        }

        [HarmonyPatch(typeof(InventoryGui), "MoveToUpperInventoryGrid")]
        private static class OutOfWardrobe
        {
            private static bool Prefix(InventoryGui __instance, Vector2i previousGridPosition)
            {
                if (ContainerGrid(__instance) != shaped || shaped == null) return true;
                if (!__instance.m_inventoryGroup.IsActive) return false;
                var player = PlayerGrid(__instance);
                int width = GridInventory(player).GetWidth();
                var selection = Selected(player);
                selection.x = Mathf.Clamp(previousGridPosition.x, 0, width - 1);
                player.SetGamepadSelection(selection);
                SetActiveGroup(__instance, ActiveGroup(__instance) + 1, true);
                return false;
            }
        }
    }
}
