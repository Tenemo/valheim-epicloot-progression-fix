using System;
using EpicLoot;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(InventoryGui), "Update")]
    internal static class InventorySlotKeys
    {
        private static readonly AccessTools.FieldRef<InventoryGui, Animator> Animator =
            AccessTools.FieldRefAccess<InventoryGui, Animator>("m_animator");
        private static readonly Action<Inventory, bool, bool> Changed =
            AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(AccessTools.Method(typeof(Inventory), "Changed"));

        private static bool InputAllowed(InventoryGui gui, GameObject drag)
        {
            var player = Player.m_localPlayer;
            if (player == null || !InventoryGui.IsVisible() || !Animator(gui).GetBool("visible") ||
                gui.m_playerGrid.GetInventory() != player.GetInventory() || drag != null || gui.m_splitDialog.IsActive ||
                InventoryPromptHost.IsOpen || player.IsDead() || player.InCutscene() || player.IsTeleporting() ||
                (Chat.instance != null && Chat.instance.HasFocus()) || Console.IsVisible() || Menu.IsVisible() ||
                TextInput.IsVisible() || StoreGui.IsVisible() || Minimap.IsOpen() ||
                (TextViewer.instance != null && TextViewer.instance.IsVisible()) || GameCamera.InFreeFly() ||
                Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) ||
                Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return false;
            var selected = EventSystem.current?.currentSelectedGameObject;
            return selected == null ||
                !((selected.GetComponentInParent<InputField>()?.isFocused ?? false) ||
                  (selected.GetComponentInParent<TMP_InputField>()?.isFocused ?? false));
        }

        // The socket whose top-row or numpad number went down this frame, among the first nine, or -1.
        private static int PressedSocket(int sockets)
        {
            for (int slot = 0; slot < Math.Min(9, sockets); slot++)
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + slot)) ||
                    Input.GetKeyDown((KeyCode)((int)KeyCode.Keypad1 + slot))) return slot;
            return -1;
        }

        private static void Postfix(InventoryGui __instance, GameObject ___m_dragGo)
        {
            if (!InputAllowed(__instance, ___m_dragGo)) return;
            Vector3 position = Input.mousePosition;
            var cursor = new Vector2i(Mathf.RoundToInt(position.x), Mathf.RoundToInt(position.y));
            var input = __instance.m_playerGrid.GetItem(cursor);
            var chestGrid = GameFields.ContainerGrid(__instance);
            var chestItem = input == null && __instance.IsContainerOpen() && chestGrid.gameObject.activeInHierarchy ? chestGrid.GetItem(cursor) : null;

            // With sockets open, the number keys over the socket row or a stone or rune in the bag or an open chest belong to
            // the sockets, even when that stone, rune or socket cannot be used; any other item still goes to the hotbar.
            if (OpenSockets.Row != null || OpenSockets.Item != null)
            {
                var grid = OpenSockets.Grid;
                var socketed = grid != null && grid.gameObject.activeInHierarchy && grid.GetInventory() == OpenSockets.Row ? grid.GetItem(cursor) : null;
                if (socketed != null || ShardstoneShortcuts.Socketable(input) || ShardstoneShortcuts.Socketable(chestItem))
                {
                    if (!ShardstoneShortcuts.Open(__instance)) return;
                    int slot = PressedSocket(OpenSockets.Row.GetWidth());
                    if (slot < 0) return;
                    // A rune or stone pointed at in the open socket row moves to the pressed socket; one in the bag or the
                    // chest goes into it.
                    if (socketed != null) ShardstoneShortcuts.Move(__instance, socketed, slot);
                    else if (input != null) ShardstoneShortcuts.Place(__instance, input, slot);
                    else ShardstoneShortcuts.PlaceFromChest(__instance, chestItem, slot);
                    return;
                }
            }

            var inventory = Player.m_localPlayer.GetInventory();
            if (input == null)
            {
                if (chestItem == null) return;
                for (int slot = 0; slot < Math.Min(8, inventory.GetWidth()); slot++)
                    if (ZInput.GetButtonDown("Hotbar" + (slot + 1)) || ZInput.GetButtonDown("Hotbar" + (slot + 1) + "Alt"))
                    { ChestHotbarTransfer.Start(__instance, chestItem, slot); return; }
                return;
            }
            if (input.m_gridPos.y < 0 || input.m_gridPos.y >= (EquipmentAndQuickSlots.VisibleRows?.Invoke() ?? inventory.GetHeight())) return;
            for (int slot = 0; slot < Math.Min(8, inventory.GetWidth()); slot++)
                if (ZInput.GetButtonDown("Hotbar" + (slot + 1)) || ZInput.GetButtonDown("Hotbar" + (slot + 1) + "Alt"))
                {
                    var destination = new Vector2i(slot, 0);
                    // An empty cell kept for a thrown recalling weapon stays free for its return.
                    if (input.m_gridPos == destination || RecallRecovery.Reserves(inventory, destination)) return;
                    var occupant = inventory.GetItemAt(slot, 0);
                    if (occupant != null) occupant.m_gridPos = input.m_gridPos;
                    input.m_gridPos = destination;
                    // Keep whole stacks, item identities and equipment state; publish the complete exchange once.
                    Changed(inventory, false, false);
                    return;
                }
        }
    }
}
