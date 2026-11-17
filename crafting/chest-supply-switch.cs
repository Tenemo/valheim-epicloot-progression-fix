using System;
using System.Collections.Generic;
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
        // A key's input path comes from the game's fixed tables; the hotkey checks ask for it every frame.
        private static readonly Dictionary<KeyCode, string> Paths = new Dictionary<KeyCode, string>();
        private static readonly AccessTools.StructFieldRef<KeyboardShortcut, KeyCode[]> Keys = AccessTools.StructFieldRefAccess<KeyboardShortcut, KeyCode[]>("_allKeys");

        private static string PathOf(KeyCode key)
        {
            if (!Paths.TryGetValue(key, out string path)) Paths[key] = path = KeyCodeToPath(key, false);
            return path;
        }

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
            if (__0.MainKey == KeyCode.None || !HasPriority(PathOf(__0.MainKey)))
                return true;
            __result = false;
            return false;
        }

        internal static bool HasPriority(string actionPath)
        {
            if (toggleKey == null) return false;
            var toggle = toggleKey.Value;
            return toggle.MainKey != KeyCode.None &&
                string.Equals(actionPath, PathOf(toggle.MainKey), StringComparison.OrdinalIgnoreCase) &&
                ModifiersHeld(toggle);
        }

        private static bool EitherShiftForToggle(KeyboardShortcut __0, ref bool __result)
        {
            var toggle = toggleKey.Value;
            if (__0.MainKey != toggle.MainKey || !SameModifiers(__0, toggle) || !NamesShift(toggle)) return true;
            __result = toggle.MainKey != KeyCode.None && Input.GetKeyDown(toggle.MainKey) && ModifiersHeld(toggle);
            return false;
        }

        // A shortcut's modifiers are its keys after the first, read without building an enumeration.
        private static KeyCode[] AllKeys(KeyboardShortcut shortcut) => Keys(ref shortcut) ?? Array.Empty<KeyCode>();

        private static bool SameModifiers(KeyboardShortcut first, KeyboardShortcut second)
        {
            var one = AllKeys(first); var other = AllKeys(second);
            if (Math.Max(one.Length, 1) != Math.Max(other.Length, 1)) return false;
            for (int index = 1; index < one.Length; index++)
                if (one[index] != other[index]) return false;
            return true;
        }

        private static bool NamesShift(KeyboardShortcut shortcut)
        {
            var keys = AllKeys(shortcut);
            for (int index = 1; index < keys.Length; index++)
                if (keys[index] == KeyCode.LeftShift || keys[index] == KeyCode.RightShift) return true;
            return false;
        }

        private static bool ModifiersHeld(KeyboardShortcut shortcut)
        {
            var keys = AllKeys(shortcut);
            for (int index = 1; index < keys.Length; index++)
                if (!ModifierHeld(keys[index])) return false;
            return true;
        }

        private static bool ModifierHeld(KeyCode key) => key == KeyCode.LeftShift || key == KeyCode.RightShift
            ? Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
            : Input.GetKey(key);
    }
}
