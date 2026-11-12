using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Player), nameof(Player.CanSwitchPVP))]
    internal static class PvpToggleCooldown
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var limits = code.Where(instruction => instruction.LoadsConstant(10f)).ToList();
            if (limits.Count != 1) throw new InvalidOperationException("Player.CanSwitchPVP no longer has one ten-second combat cooldown");
            limits[0].operand = 1f;
            return code;
        }
    }

    internal static class PvpPersistence
    {
        private const string Key = Plugin.Id + ".pvp";

        [HarmonyPatch(typeof(Player), nameof(Player.Save))]
        private static class Save
        {
            private static void Prefix(Player __instance)
            {
                var view = GameFields.CharacterView(__instance);
                if (view && view.IsValid()) __instance.m_customData[Key] = __instance.IsPVPEnabled() ? "1" : "0";
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.Load))]
        private static class Load
        {
            private static void Postfix(Player __instance)
            {
                // Character previews also load player data, without a network object to publish PvP on.
                var view = GameFields.CharacterView(__instance);
                if (view && view.IsValid() && __instance.m_customData.TryGetValue(Key, out string saved)) __instance.SetPVP(saved == "1");
            }
        }

        // The native inventory applies its toggle to the player every visible frame. Initialize it from the character
        // each time it opens, before that first frame can overwrite a loaded choice.
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        private static class Inventory
        {
            private static void Prefix(InventoryGui __instance)
            {
                if (Player.m_localPlayer) __instance.m_pvp.SetIsOnWithoutNotify(Player.m_localPlayer.IsPVPEnabled());
            }
        }
    }
}
