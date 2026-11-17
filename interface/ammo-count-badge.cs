using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Ammo Count 0.5.1 shows a badge, the icon and stack size of the ammunition an item would use next, above every
    // hotbar and quick slot item with an ammo type except arrows and bolts themselves. Meads carry the ammo type of the
    // tankards that drink them, as does the Corked Vial, a crafting material, and ballista bolts carry one of their own,
    // so each of them showed the first matching item in the inventory. Only a weapon's attack draws ammunition, so show
    // the badge only on weapons, such as bows, crossbows, fishing rods and tankards. Its count was the size of the one
    // stack the next attack draws from, so 1000 and 70 arrows of a kind read 70 while the 70 were equipped; count them all.
    internal static class AmmoCountBadge
    {
        // Slot units between the icon and the count.
        private const float IconGap = 2f;
        private static readonly AccessTools.FieldRef<HotkeyBar, object> Elements = AccessTools.FieldRefAccess<HotkeyBar, object>("m_elements");
        private static readonly ConditionalWeakTable<HotkeyBar, BarState> Bars = new ConditionalWeakTable<HotkeyBar, BarState>();
        private static readonly Dictionary<string, string> Counts = new Dictionary<string, string>();
        private static AccessTools.FieldRef<bool> reset;
        private static int inventoryVersion, configurationVersion, countedVersion = -1;
        private static Inventory countedInventory;

        private sealed class BarState
        {
            internal Player Player;
            internal ItemDrop.ItemData Ammo;
            internal object FirstElement;
            internal int ElementCount, InventoryVersion = -1, ConfigurationVersion = -1;
            internal bool AfterReset, Conjuring;
            internal readonly List<(ItemDrop.ItemData Item, Vector2i Position)> Items = new List<(ItemDrop.ItemData, Vector2i)>();
        }

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.AmmoCount, out var plugin)) return;
            var badge = AccessTools.Inner(plugin.Instance.GetType(), "HotkeyBar_Update_Patch");
            var checkAmmo = badge == null ? null : AccessTools.Method(badge, "CheckAmmo", new[] { typeof(ItemDrop.ItemData), typeof(GameObject) });
            var update = badge == null ? null : AccessTools.Method(badge, "Postfix", new[] { typeof(HotkeyBar), typeof(List<ItemDrop.ItemData>) });
            if (checkAmmo == null || !checkAmmo.IsStatic || update == null) throw new InvalidOperationException("Ammo Count's badge check moved.");
            reset = AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(plugin.Instance.GetType(), "reset"));
            harmony.Patch(checkAmmo, prefix: new HarmonyMethod(typeof(AmmoCountBadge), nameof(OnlyOnWeapons)),
                postfix: new HarmonyMethod(typeof(AmmoCountBadge), nameof(LayOut)),
                transpiler: new HarmonyMethod(typeof(AmmoCountBadge), nameof(CountEveryStack)));
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(AmmoCountBadge), nameof(RefreshNeeded)));
            harmony.Patch(AccessTools.Method(typeof(Inventory), "Changed"), postfix: new HarmonyMethod(typeof(AmmoCountBadge), nameof(InventoryChanged)));
            EventHandler<SettingChangedEventArgs> changed = (sender, arguments) => configurationVersion++;
            IntegrationChanges.Defer(() => plugin.Instance.Config.SettingChanged += changed, () => plugin.Instance.Config.SettingChanged -= changed);
            log.LogInfo("Ammo Count's badge shows only on weapons, counts every stack of the ammunition, and scales with its slot with the icon beside the count.");
        }

        private static void InventoryChanged() => inventoryVersion++;

        // The upstream postfix scans every slot and rewrites its badge each frame. Native bars rebuild all their elements
        // together; item identity and position also catch quick-slot remapping without an inventory notification.
        private static bool RefreshNeeded(HotkeyBar __0, List<ItemDrop.ItemData> __1)
        {
            var player = Player.m_localPlayer;
            if (!player) return true;
            var state = Bars.GetValue(__0, _ => new BarState());
            var elements = (IList)Elements(__0);
            object first = elements.Count == 0 ? null : elements[0];
            var ammo = player.GetAmmoItem();
            bool requestedReset = reset();
            // Equipping a set piece changes no inventory, but it can start or end conjuring.
            bool conjuring = player.HasActiveMagicEffect(MagicEffectType.ConjuredArrows);
            bool changed = state.Player != player || state.Ammo != ammo || state.Conjuring != conjuring || state.InventoryVersion != inventoryVersion ||
                state.ConfigurationVersion != configurationVersion || !ReferenceEquals(state.FirstElement, first) ||
                state.ElementCount != elements.Count || state.Items.Count != __1.Count || requestedReset || state.AfterReset;
            if (!changed)
                for (int index = 0; index < __1.Count; index++)
                    if (!ReferenceEquals(state.Items[index].Item, __1[index]) || state.Items[index].Position != __1[index].m_gridPos) { changed = true; break; }
            if (!changed) return false;
            state.Player = player; state.Ammo = ammo; state.Conjuring = conjuring; state.InventoryVersion = inventoryVersion; state.ConfigurationVersion = configurationVersion;
            state.FirstElement = first; state.ElementCount = elements.Count;
            // Destroy is deferred: let the following frame recreate badges removed by the reset pass.
            state.AfterReset = requestedReset;
            state.Items.Clear(); foreach (var item in __1) state.Items.Add((item, item.m_gridPos));
            return true;
        }

        // Humanoid.GetCurrentWeapon attacks only with items IsWeapon accepts, and only attacks draw ammunition, which a
        // bow conjuring its arrows doesn't spend.
        private static bool OnlyOnWeapons(ItemDrop.ItemData __0, GameObject __1)
        {
            if (__0.IsWeapon() && !ConjuredArrows.TryGetShotCost(__0, out _)) return true;
            __1.SetActive(false);
            return false;
        }

        // The count is ammo.m_stack.ToString(), ammo being the item the next attack draws from; write its total instead.
        private static IEnumerable<CodeInstruction> CountEveryStack(IEnumerable<CodeInstruction> instructions)
        {
            var stack = AccessTools.Field(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_stack));
            var text = AccessTools.Method(typeof(int), nameof(int.ToString), Type.EmptyTypes);
            var code = instructions.ToList();
            var counts = Enumerable.Range(0, code.Count - 1)
                .Where(index => code[index].LoadsField(stack, byAddress: true) && code[index + 1].Calls(text)).ToList();
            if (counts.Count != 1) throw new InvalidOperationException("Ammo Count's count changed.");
            int count = counts[0];
            code[count].opcode = OpCodes.Call;
            code[count].operand = AccessTools.Method(typeof(AmmoCountBadge), nameof(Total));
            code[count + 1].opcode = OpCodes.Nop;
            code[count + 1].operand = null;
            return code;
        }

        // Every stack of that ammunition the player carries, quick slots included, whatever its world level, as attacks
        // draw from them all.
        private static string Total(ItemDrop.ItemData ammo)
        {
            var inventory = Player.m_localPlayer.GetInventory();
            if (countedInventory != inventory || countedVersion != inventoryVersion)
            {
                Counts.Clear(); countedInventory = inventory; countedVersion = inventoryVersion;
            }
            string name = ammo.m_shared.m_name;
            if (!Counts.TryGetValue(name, out string count)) Counts[name] = count = inventory.CountItems(name, matchWorldLevel: false).ToString();
            return count;
        }

        // Ammo Count parents the badge keeping its size on screen, so the badge stayed the same number of pixels at every
        // resolution and GUI scale while the slot under it scaled, and no offset in its config could follow the slot's
        // edge. Scale the badge with the slot, so the config is in slot units with the slot 64 wide. The pack right-aligns
        // the count to the slot's edge, so the count grows leftward with its digits; keep the icon just left of it.
        private static void LayOut(GameObject __1)
        {
            if (!__1.activeSelf) return;
            if (__1.transform.localScale != Vector3.one) __1.transform.localScale = Vector3.one;
            if (!(__1.transform.Find("Icon") is RectTransform icon)) return;
            var count = __1.transform.Find("Text").GetComponent<TMP_Text>();
            var box = count.rectTransform;
            float left = box.anchoredPosition.x + box.rect.xMax - count.preferredWidth;
            var position = new Vector2(left - IconGap - icon.rect.width / 2f, icon.anchoredPosition.y);
            if (icon.anchoredPosition != position) icon.anchoredPosition = position;
        }
    }
}
