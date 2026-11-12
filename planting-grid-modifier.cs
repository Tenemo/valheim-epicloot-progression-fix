using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Valheim.SettingsGui;

namespace EpicLootProgressionFix
{
    // PlantEasily's grid resize modifier, Right Ctrl unless a player changed its config, is a personal control in Settings,
    // Keyboard & Mouse, so players without a right Ctrl can pick any keyboard key or mouse button. An untouched control keeps
    // the player's PlantEasily setting; once saved, the native binding wins, including a cleared one. Arrows, grid limits and
    // the gamepad modifier are PlantEasily's own.
    internal static class PlantingGridModifier
    {
        internal const string Button = "EpicLootProgressionFix.PlantingGridModifier";
        private static Func<KeyCode> LegacyKey;
        private static FieldInfo keyboardHint, modifierHeld;
        private static int lastInputFrame = -2;
        private static readonly Func<KeyCode, bool, string> KeyCodeToPath =
            AccessTools.MethodDelegate<Func<KeyCode, bool, string>>(AccessTools.Method(typeof(ZInput), "KeyCodeToPath"));

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null ||
                !PluginIds.TryGet(PluginIds.PlantEasily, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var context = assembly.GetType("Advize_PlantEasily.ModContext", true);
            var config = AccessTools.Field(context, "config") ?? throw new InvalidOperationException("PlantEasily's configuration changed.");
            var legacy = AccessTools.PropertyGetter(config.FieldType, "KeyboardModifierKey") ?? throw new InvalidOperationException("PlantEasily's modifier setting changed.");
            LegacyKey = () => (KeyCode)legacy.Invoke(config.GetValue(null), null);
            var hints = assembly.GetType("Advize_PlantEasily.KeyHintPatches", true);
            keyboardHint = AccessTools.Field(hints, "_keyboardHint") ?? throw new InvalidOperationException("PlantEasily's grid hint changed.");
            harmony.Patch(AccessTools.Method(typeof(ZInput), "ResetKBMButtons"), postfix: new HarmonyMethod(typeof(PlantingGridModifier), nameof(RegisterButton)));
            harmony.Patch(AccessTools.Method(typeof(KeyboardMouseSettings), "SetupKeys"), prefix: new HarmonyMethod(typeof(PlantingGridModifier), nameof(AddControl)));
            var input = assembly.GetType("Advize_PlantEasily.InputPatches", true);
            modifierHeld = AccessTools.Field(input, "modifierHeld") ?? throw new InvalidOperationException("PlantEasily's modifier state changed.");
            harmony.Patch(AccessTools.Method(input, "Prefix", new[] { typeof(Player) }),
                prefix: new HarmonyMethod(typeof(PlantingGridModifier), nameof(ReleaseAfterInterruption)),
                transpiler: new HarmonyMethod(typeof(PlantingGridModifier), nameof(UseNativeModifier)));
            harmony.Patch(AccessTools.Method(hints, "UpdateKeyboardHints"), postfix: new HarmonyMethod(typeof(PlantingGridModifier), nameof(ShowNativeModifier)));
            if (ZInput.instance != null) RegisterButton(ZInput.instance);
            log.LogInfo("PlantEasily's planting grid modifier is a personal control in Settings, Keyboard & Mouse.");
        }

        private static void RegisterButton(ZInput __instance)
        {
            if (__instance.GetButtonDef(Button) != null) return;
            NativeControlRows.AddButton(__instance, Button, KeyCodeToPath(LegacyKey(), false), false, true, true, 0f, 0f);
            // ZInput.Load applies saved bindings only to buttons that already existed when it ran.
            if (PlatformPrefs.HasKey("kbmBinding_" + Button)) __instance.GetButtonDef(Button).Rebind(PlatformPrefs.GetString("kbmBinding_" + Button));
        }

        private static void AddControl(KeyboardMouseSettings __instance) =>
            NativeControlRows.Add(__instance, Button, "PlantingGridModifier", "Planting grid modifier");

        // PlantEasily holds the modifier from its key's press until its release, read only while the player takes build input.
        // A release behind the menu, Settings, a rebind, the inventory or chat is never seen, so any frame it skipped ends the
        // hold, keyboard or gamepad; a button held through such a window must be pressed again.
        private static void ReleaseAfterInterruption()
        {
            if (Time.frameCount - lastInputFrame > 1) modifierHeld.SetValue(null, false);
            lastInputFrame = Time.frameCount;
        }

        // Read the native control's press and release instead of the configured key.
        private static IEnumerable<CodeInstruction> UseNativeModifier(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var down = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetKeyDown), new[] { typeof(KeyCode), typeof(bool) });
            var up = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetKeyUp), new[] { typeof(KeyCode), typeof(bool) });
            int replaced = 0;
            for (int index = 0; index < code.Count; index++)
            {
                if (!(code[index].operand is MethodInfo getter) || getter.Name != "get_KeyboardModifierKey") continue;
                for (int next = index + 1; next < Math.Min(code.Count, index + 4); next++)
                {
                    if (code[next].Calls(down)) code[next].operand = AccessTools.Method(typeof(PlantingGridModifier), nameof(Pressed));
                    else if (code[next].Calls(up)) code[next].operand = AccessTools.Method(typeof(PlantingGridModifier), nameof(Released));
                    else continue;
                    code[next].opcode = OpCodes.Call;
                    replaced++;
                    break;
                }
            }
            if (replaced != 2) throw new InvalidOperationException("PlantEasily's grid modifier input changed.");
            return code;
        }

        private static bool Pressed(KeyCode _, bool __) => ZInput.GetButtonDown(Button);

        private static bool Released(KeyCode _, bool __) => ZInput.GetButtonUp(Button);

        // The cultivator's grid hint names the native control, refreshed whenever the key hints apply the settings.
        private static void ShowNativeModifier()
        {
            var hint = keyboardHint.GetValue(null) as GameObject;
            if (!hint || ZInput.instance == null) return;
            hint.transform.GetChild(1).GetChild(0).GetComponent<TextMeshProUGUI>().text = Localization.instance.Localize(ZInput.instance.GetBoundKeyString(Button, true));
        }
    }
}
