using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
    internal static class BoatCamera
    {
        private static readonly AccessTools.FieldRef<GameCamera, float> Distance =
            AccessTools.FieldRefAccess<GameCamera, float>("m_distance");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var landLimit = AccessTools.Field(typeof(GameCamera), nameof(GameCamera.m_maxDistance));
            var boatLimit = AccessTools.Field(typeof(GameCamera), nameof(GameCamera.m_maxDistanceBoat));
            var code = Transpilers.CallInstead(instructions, x => x.LoadsField(landLimit) || x.LoadsField(boatLimit),
                AccessTools.Method(typeof(BoatCamera), nameof(MaximumDistance)), 2, "Valheim's camera zoom limits changed.");
            // Native zoom clamping is inside the input gate. Apply the maximum afterward as well, so leaving
            // a ship restores the land cap while an inventory, menu or build selection is open.
            int afterInput = code.FindIndex(instruction => instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.IsDead)))) - 1;
            if (afterInput < 0) throw new InvalidOperationException("Valheim's camera update after zoom input changed.");
            var clamp = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code[afterInput]);
            code.InsertRange(afterInput, new[] { clamp,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoatCamera), nameof(ClampMaximumDistance))) });
            return code;
        }

        private static void ClampMaximumDistance(GameCamera camera) =>
            Distance(camera) = Mathf.Min(Distance(camera), MaximumDistance(camera));

        // Keep the native boat field unchanged: camera tilt also uses it. The ship's onboard volume
        // includes seated and standing passengers, and stays active during a jump on deck.
        private static float MaximumDistance(GameCamera camera) =>
            Ship.GetLocalShip() != null || Player.m_localPlayer.GetControlledShip() != null
                ? Plugin.ShipZoomDistance.Value : camera.m_maxDistance * Plugin.LandZoomMultiplier.Value;
    }
}
