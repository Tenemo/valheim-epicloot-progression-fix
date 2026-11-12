using System;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // EquipmentAndQuickSlots suppresses V's vanilla action only while its slot contains an item. Once the food runs
    // out, trying to eat again can turn auto pickup off. Give that binding its own key, including after a controls
    // reset or a saved-settings reload. Other personal bindings and the player's on/off choice stay unchanged.
    internal static class AutoPickupKey
    {
        private static readonly ConfigEntry<KeyboardShortcut>[] quickSlotKeys = new ConfigEntry<KeyboardShortcut>[6];

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.EquipmentAndQuickSlots, out var slots)) return;
            for (int index = 0; index < quickSlotKeys.Length; index++)
                if (!slots.Instance.Config.TryGetEntry("Hotkeys", $"Quick slot hotkey {index + 1}", out quickSlotKeys[index]))
                    throw new InvalidOperationException("EquipmentAndQuickSlots' hotkey settings moved.");

            foreach (string name in new[] { "Load", "Reset", "ResetToDefault", "OnRebindComplete" })
            {
                var method = AccessTools.Method(typeof(ZInput), name);
                if (method == null) throw new MissingMethodException(typeof(ZInput).FullName, name);
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(AutoPickupKey), nameof(SeparateFromQuickSlot)));
            }
            if (ZInput.instance != null) SeparateFromQuickSlot(ZInput.instance);
            log.LogInfo("Auto pickup uses right bracket when V overlaps a quick slot; other personal bindings stay unchanged.");
        }

        private static void SeparateFromQuickSlot(ZInput __instance)
        {
            if (!quickSlotKeys.Any(key => key.Value.MainKey == KeyCode.V)) return;
            var button = __instance.GetButtonDef("AutoPickup");
            if (button != null && string.Equals(button.GetActionPath(true), "<Keyboard>/v", StringComparison.OrdinalIgnoreCase))
                button.Rebind("<Keyboard>/rightBracket");
        }
    }
}
