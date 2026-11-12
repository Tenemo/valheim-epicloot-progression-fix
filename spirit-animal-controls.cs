using System.Linq;
using BepInEx.Configuration;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(SpiritAnimal), "CanUseBindKey")]
    internal static class SpiritAnimalControls
    {
        private static void Postfix(ref bool __result)
        {
            if (!__result || !PluginIds.TryGet(PluginIds.TameRiding, out var plugin)) return;
            var enabled = (ConfigEntry<bool>)plugin.Instance.Config[new ConfigDefinition("Sit", "Enabled")];
            var shortcut = ((ConfigEntry<KeyboardShortcut>)plugin.Instance.Config[new ConfigDefinition("Sit", "Keybind")]).Value;
            if (enabled.Value && shortcut.MainKey != KeyCode.None && ZInput.GetKey(shortcut.MainKey) && shortcut.Modifiers.All(Held))
                __result = false;
        }

        private static bool Held(KeyCode key) => key == KeyCode.LeftShift || key == KeyCode.RightShift
            ? ZInput.GetKey(KeyCode.LeftShift) || ZInput.GetKey(KeyCode.RightShift)
            : key == KeyCode.LeftControl || key == KeyCode.RightControl
                ? ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl)
                : key == KeyCode.LeftAlt || key == KeyCode.RightAlt
                    ? ZInput.GetKey(KeyCode.LeftAlt) || ZInput.GetKey(KeyCode.RightAlt) : ZInput.GetKey(key);
    }
}
