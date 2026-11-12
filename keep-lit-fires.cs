using System;
using System.Collections.Generic;
using BepInEx.Logging;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // NoSmokeStayLit 2.4.2 keeps the torch types it lists lit by filling their fuel on every player's game every 2
    // seconds, and the game that owns a torch then burned a little of it and recorded the time. Each torch changed every
    // 2 seconds, and every other nearby player's game changed it back, each change going to the server and on to every
    // nearby player. A keep-lit torch's owner now only fills its fuel when it is below full, such as after building, and
    // otherwise leaves it alone. Other fires burn fuel as in the game. Lit state, smoke and hover text are unchanged.
    internal static class KeepLitFires
    {
        private static Func<string, bool> keepLit;
        private static ConfigEntry<KeyCode> toggleKey;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.NoSmokeStayLit, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var check = AccessTools.Method(assembly.GetType("NoSmokeStayLit.Patches.Configs"), "ConfigCheck", new[] { typeof(string) });
            var refuel = AccessTools.Method(assembly.GetType("NoSmokeStayLit.Patches.KeepFueledPatch+Fireplace_UpdateFireplace_Patch"),
                "Postfix");
            if (check == null || !check.IsStatic || check.ReturnType != typeof(bool) || refuel == null || refuel.ReturnType != typeof(void))
                throw new InvalidOperationException("NoSmokeStayLit's keep-lit fires changed.");
            keepLit = AccessTools.MethodDelegate<Func<string, bool>>(check);
            toggleKey = (ConfigEntry<KeyCode>)AccessTools.Field(assembly.GetType("NoSmokeStayLit.Patches.Configs"), "stationToggleKey").GetValue(null);
            // Another prefix cannot stop NoSmokeStayLit's, so its refuelling itself is skipped.
            harmony.Patch(refuel, prefix: new HarmonyMethod(typeof(KeepLitFires), nameof(SkipRefuel)));
            foreach (string hover in new[] { "Fireplace_GetHoverText_Patch", "Smelter_GetHoverTextFuel_Patch" })
                harmony.Patch(AccessTools.Method(assembly.GetType("NoSmokeStayLit.Patches.Interface_Patch+" + hover), "Postfix"),
                    prefix: new HarmonyMethod(typeof(KeepLitFires), nameof(ShowToggle)));
            harmony.Patch(AccessTools.Method(typeof(Fireplace), "UpdateFireplace"),
                transpiler: new HarmonyMethod(typeof(KeepLitFires), nameof(Transpiler)));
            log.LogInfo("NoSmokeStayLit's keep-lit torches stay full without changing every 2 seconds.");
        }

        private static bool SkipRefuel() => false;
        private static bool ShowToggle() => toggleKey.Value != KeyCode.None;

        internal static bool IsKeptLit(Fireplace fire) => keepLit?.Invoke(fire.name) == true;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.ReplaceOwnerCheck(instructions, typeof(Fireplace), AccessTools.Method(typeof(KeepLitFires), nameof(OwnerBurnsFuel)), true);

        // Takes the place of m_nview.IsOwner(): true burns fuel as in the game, false goes straight to the lit state.
        private static bool OwnerBurnsFuel(ZNetView view, Fireplace fire)
        {
            if (!view.IsOwner()) return false;
            if (!IsKeptLit(fire)) return true;
            // An unchanged value is not a change, so a full torch sends nothing.
            view.GetZDO().Set(ZDOVars.s_fuel, fire.m_maxFuel);
            return false;
        }
    }
}
