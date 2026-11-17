using System;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Ship Stats 1.0.8 shows its panel beside the rudder when the player boards, then hides it whenever the player leaves
    // any ship's boarding area or any ship the player's game owns is destroyed, even while still steering; only the next
    // boarding showed it again. Those events now leave the panel alone while the player steers, and the ship HUD shows it
    // again whenever it is off. The ship HUD, panel included, still hides when the player stops steering, also when the
    // steered ship is destroyed.
    internal static class ShipStatsHelm
    {
        private static AccessTools.FieldRef<GameObject> panel;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.ShipStats, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var field = AccessTools.Field(assembly.GetType("ShipStats.HudAwakePatch"), "Go");
            var events = assembly.GetType("ShipStats.ShipOnTriggerExitDestroyedPatch");
            var exit = AccessTools.Method(events, "Postfix1");
            var destroyed = AccessTools.Method(events, "Postfix2");
            if (field?.FieldType != typeof(GameObject) || exit == null || destroyed == null) throw new InvalidOperationException("Ship Stats' rudder panel changed.");
            panel = AccessTools.StaticFieldRefAccess<GameObject>(field);
            harmony.Patch(exit, prefix: new HarmonyMethod(typeof(ShipStatsHelm), nameof(Unsteered)));
            harmony.Patch(destroyed, prefix: new HarmonyMethod(typeof(ShipStatsHelm), nameof(Unsteered)));
            harmony.Patch(AccessTools.Method(typeof(Hud), "UpdateShipHud"), postfix: new HarmonyMethod(typeof(ShipStatsHelm), nameof(ShowWhileSteering)));
        }

        private static bool Unsteered() => !Player.m_localPlayer || !Player.m_localPlayer.GetControlledShip();

        private static void ShowWhileSteering(Player player)
        {
            var stats = panel();
            if (stats && !stats.activeSelf && player.GetControlledShip()) stats.SetActive(true);
        }
    }
}
