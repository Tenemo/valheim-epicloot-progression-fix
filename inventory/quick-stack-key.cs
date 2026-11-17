using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Valheim.SettingsGui;

namespace EpicLootProgressionFix
{
    // Native controls are saved in PlatformPrefs, outside the config files Gale imports and publishes.
    // Keep the shared Quick Stack config at P and use the player's own binding for its keyboard/mouse shortcut.
    internal static class QuickStackKey
    {
        internal const string QuickStackButton = "EpicLootProgressionFix.QuickStack";
        private static FieldInfo quickStackKey;
        private static AccessTools.FieldRef<ConfigEntry<KeyboardShortcut>> ControllerKey;
        private static Func<KeyboardShortcut, bool> ControllerKeyDown;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null ||
                !PluginIds.TryGet(PluginIds.QuickStack, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            quickStackKey = AccessTools.Field(assembly.GetType("QuickStackStore.QSSConfig+QuickStackConfig", true), "QuickStackKeybind");
            ControllerKey = AccessTools.StaticFieldRefAccess<ConfigEntry<KeyboardShortcut>>(quickStackKey);
            ControllerKeyDown = AccessTools.MethodDelegate<Func<KeyboardShortcut, bool>>(
                AccessTools.Method(assembly.GetType("QuickStackStore.KeybindChecker", true), "IsKeyDown"));
            harmony.Patch(AccessTools.Method(typeof(ZInput), "ResetKBMButtons"),
                postfix: new HarmonyMethod(typeof(QuickStackKey), nameof(RegisterButton)));
            harmony.Patch(AccessTools.Method(typeof(KeyboardMouseSettings), "SetupKeys"),
                prefix: new HarmonyMethod(typeof(QuickStackKey), nameof(AddControl)));
            harmony.Patch(AccessTools.Method(assembly.GetType("QuickStackStore.KeybindChecker+Player_Update_Patch", true), "HandleGenericKeys"),
                transpiler: new HarmonyMethod(typeof(QuickStackKey), nameof(UsePersonalBinding)));
            harmony.Patch(AccessTools.Method(assembly.GetType("QuickStackStore.KeybindChecker", true), "IgnoreKeyPressesDueToPlayer"),
                transpiler: new HarmonyMethod(typeof(QuickStackKey), nameof(AllowQuickStackWithBuildTool)));
            if (ZInput.instance != null)
            {
                RegisterButton(ZInput.instance);
                ZInput.instance.GetButtonDef(QuickStackButton).Rebind(PlatformPrefs.GetString("kbmBinding_" + QuickStackButton, ""));
            }
            log.LogInfo("Quick stack is a personal control in Settings, Keyboard & Mouse, with P as the default.");
        }

        private static void RegisterButton(ZInput __instance)
        {
            if (__instance.GetButtonDef(QuickStackButton) == null)
                NativeControlRows.AddButton(__instance, QuickStackButton, "<Keyboard>/p", false, true, true, 0f, 0f);
        }

        private static void AddControl(KeyboardMouseSettings __instance) =>
            NativeControlRows.Add(__instance, QuickStackButton, "QuickStack", "Quick stack");

        private static IEnumerable<CodeInstruction> UsePersonalBinding(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var matches = code.Select((instruction, index) => new { instruction, index })
                .Where(value => value.instruction.LoadsField(quickStackKey)).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("Quick Stack's keyboard shortcut read changed.");
            int position = matches[0].index;
            if (position + 2 >= code.Count || code[position + 1].opcode != OpCodes.Callvirt ||
                !(code[position + 2].operand is MethodInfo check) || check.Name != "IsKeyDown")
                throw new InvalidOperationException("Quick Stack's keyboard shortcut check changed.");
            code[position].opcode = OpCodes.Call;
            code[position].operand = AccessTools.Method(typeof(QuickStackKey), nameof(QuickStackPressed));
            for (int index = position + 1; index <= position + 2; index++)
            {
                code[index].opcode = OpCodes.Nop;
                code[index].operand = null;
            }
            return code;
        }

        private static IEnumerable<CodeInstruction> AllowQuickStackWithBuildTool(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.InPlaceMode))),
                AccessTools.Method(typeof(QuickStackKey), nameof(BlockBuildToolShortcut)), 1, "Quick Stack's build tool check changed.");

        // Let only the personal quick stack shortcut through the build-tool guard; the other hotkeys keep their rules.
        private static bool BlockBuildToolShortcut(Character player) =>
            player.InPlaceMode() && (ZInput.IsGamepadActive() || !QuickStackPressed());

        private static bool QuickStackPressed()
        {
            if (Settings.instance || ZInput.s_IsRebindActive) return false;
            if (ZInput.IsGamepadActive() && (int)ControllerKey().Value.MainKey >= (int)KeyCode.JoystickButton0)
                return ControllerKeyDown(ControllerKey().Value);
            if (!ZInput.GetButtonDown(QuickStackButton)) return false;
            return !ChestSupplySwitch.HasPriority(ZInput.instance.GetButtonDef(QuickStackButton).GetActionPath(true));
        }
    }
}
