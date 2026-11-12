using System;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // AzuCraftyBoxes takes a chest out of the storage that crafting draws from, or puts it back, on its Container Toggle
    // Key, which the pack sets to Shift + L so that pressing E while sprinting opens the chest. Quick Stack's hotkeys ignore
    // modifiers they do not name, so its restock on L would also run on Shift + L. Either Shift key performs the container
    // toggle, which takes priority over Quick Stack shortcuts on that key. Other personal bindings stay unchanged.
    internal static class ChestSupplySwitch
    {
        private static ConfigEntry<KeyboardShortcut> toggleKey;
        private static readonly Func<KeyCode, bool, string> KeyCodeToPath =
            AccessTools.MethodDelegate<Func<KeyCode, bool, string>>(AccessTools.Method(typeof(ZInput), "KeyCodeToPath"));

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var plugin) ||
                !PluginIds.TryGet(PluginIds.QuickStack, out var quickStack)) return;
            if (!plugin.Instance.Config.TryGetEntry("3 - Keys", "Container Toggle Key", out toggleKey))
                throw new InvalidOperationException("AzuCraftyBoxes' container toggle key moved.");
            var keyDown = AccessTools.Method(quickStack.Instance.GetType().Assembly.GetType("QuickStackStore.KeybindChecker"),
                "IsKeyDown", new[] { typeof(KeyboardShortcut) });
            var craftyKeyDown = AccessTools.Method(plugin.Instance.GetType().Assembly.GetType("AzuCraftyBoxes.KeyboardExtensions"),
                "IsKeyDown", new[] { typeof(KeyboardShortcut) });
            if (keyDown == null || craftyKeyDown == null) throw new InvalidOperationException("A chest shortcut's hotkey check moved.");
            harmony.Patch(keyDown, prefix: new HarmonyMethod(typeof(ChestSupplySwitch), nameof(NotOnToggleKey)));
            harmony.Patch(craftyKeyDown, prefix: new HarmonyMethod(typeof(ChestSupplySwitch), nameof(EitherShiftForToggle)));
            log.LogInfo($"AzuCraftyBoxes' container toggle key, {toggleKey.Value}, accepts either Shift and takes priority over Quick Stack.");
        }

        private static bool NotOnToggleKey(KeyboardShortcut __0, ref bool __result)
        {
            if (__0.MainKey == KeyCode.None || !HasPriority(KeyCodeToPath(__0.MainKey, false)))
                return true;
            __result = false;
            return false;
        }

        internal static bool HasPriority(string actionPath)
        {
            if (toggleKey == null) return false;
            var toggle = toggleKey.Value;
            return toggle.MainKey != KeyCode.None &&
                string.Equals(actionPath, KeyCodeToPath(toggle.MainKey, false), StringComparison.OrdinalIgnoreCase) &&
                toggle.Modifiers.All(ModifierHeld);
        }

        private static bool EitherShiftForToggle(KeyboardShortcut __0, ref bool __result)
        {
            var toggle = toggleKey.Value;
            if (__0.MainKey != toggle.MainKey || !__0.Modifiers.SequenceEqual(toggle.Modifiers) ||
                !toggle.Modifiers.Any(key => key == KeyCode.LeftShift || key == KeyCode.RightShift)) return true;
            __result = toggle.MainKey != KeyCode.None && Input.GetKeyDown(toggle.MainKey) && toggle.Modifiers.All(ModifierHeld);
            return false;
        }

        private static bool ModifierHeld(KeyCode key) => key == KeyCode.LeftShift || key == KeyCode.RightShift
            ? Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
            : Input.GetKey(key);
    }
}
