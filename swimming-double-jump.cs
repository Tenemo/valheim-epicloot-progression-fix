using System;
using System.Collections.Generic;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Character), "UpdateMotion")]
    internal static class SwimmingDoubleJump
    {
        private static Player jumpingPlayer;
        private static Player trackedPlayer;
        private static bool swimmingEpisode;
        private static readonly Func<Character, bool> InSwimDepth = AccessTools.MethodDelegate<Func<Character, bool>>(
            AccessTools.Method(typeof(Character), "InLiquidSwimDepth"));

        private static void TrackLocalPlayer()
        {
            if (ReferenceEquals(trackedPlayer, Player.m_localPlayer)) return;
            trackedPlayer = Player.m_localPlayer;
            jumpingPlayer = null;
            swimmingEpisode = false;
        }

        internal static Vector3 PrepareLaunch(Character character, Vector3 velocity)
        {
            TrackLocalPlayer();
            if (character != Player.m_localPlayer || !character.IsSwimming() || character.IsOnGround() || velocity.y <= 0f)
                return velocity;
            float depth = character.GetLiquidLevel() - character.transform.position.y;
            if (depth <= 0f) return velocity;
            // Recover the submerged starting height, then retain the ordinary jump height above the surface.
            velocity.y = Mathf.Sqrt(velocity.y * velocity.y + 2f * Mathf.Max(0f, -Physics.gravity.y) * depth);
            jumpingPlayer = Player.m_localPlayer;
            return velocity;
        }

        internal static void Launched(Character character)
        {
            if (character == jumpingPlayer) swimmingEpisode = false;
        }

        private static bool SwimmingForMovement(Character character)
        {
            bool swimming = character.IsSwimming();
            TrackLocalPlayer();
            if (character != trackedPlayer) return swimming;
            if (character == jumpingPlayer)
            {
                if (swimming && !character.IsOnGround() && character.GetVelocity().y > 0f) return false;
                jumpingPlayer = null;
            }
            bool genuineSwimming = swimming && !character.IsOnGround() && InSwimDepth(character);
            if (genuineSwimming && !swimmingEpisode) DoubleJump.MultiJumpCombo = 0;
            swimmingEpisode = genuineSwimming;
            return swimming;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions,
                instruction => instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.IsSwimming))),
                AccessTools.Method(typeof(SwimmingDoubleJump), nameof(SwimmingForMovement)), 1,
                "Valheim's swimming movement selection changed.");
    }
}
