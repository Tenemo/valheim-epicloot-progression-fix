using System.Reflection;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // EpicLoot spends the Air Jump on any press the game counts as airborne: 0.2 s after the last ground contact, as
    // over a crest or a bump, or on the physics step that touches down before the landing registers. Such a press
    // is a ground jump instead, so the Air Jump stays for the second press.
    [HarmonyPatch(typeof(Character), nameof(Character.Jump))]
    internal static class NearGroundJumps
    {
        internal const float GroundRange = 1f;
        private static readonly AccessTools.FieldRef<Character, float> SinceGround = AccessTools.FieldRefAccess<Character, float>("m_lastGroundTouch");
        private static readonly AccessTools.FieldRef<Character, float> SinceJump = AccessTools.FieldRefAccess<Character, float>("m_jumpTimer");
        private static readonly AccessTools.FieldRef<Character, bool> Touching = AccessTools.FieldRefAccess<Character, bool>("m_groundContact");
        private static readonly AccessTools.FieldRef<Character, CapsuleCollider> Body = AccessTools.FieldRefAccess<Character, CapsuleCollider>("m_collider");
        private static readonly FieldInfo GroundMask = AccessTools.Field(typeof(Character), "s_groundRayMask");

        // Runs before EpicLoot's prefix, which reads IsOnGround to choose between the ground and the air jump.
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Character __instance, out float __state)
        {
            __state = -1f;
            if (__instance != Player.m_localPlayer || __instance.IsOnGround() || GrapplingPoint.m_localGrappler != null) return;
            bool nearGround = Touching(__instance)
                || !__instance.IsSwimming() && SinceJump(__instance) > SinceGround(__instance) && GroundBelow(__instance);
            if (!nearGround || !Player.m_localPlayer.HasActiveMagicEffect(MagicEffectType.DoubleJump)) return;
            __state = SinceGround(__instance);
            SinceGround(__instance) = 0f;
        }

        // ForceJump sets the time since ground contact to 1 s; a press that launched nothing keeps its airborne time.
        private static void Postfix(Character __instance, float __state)
        {
            if (__state >= 0f && SinceGround(__instance) == 0f) SinceGround(__instance) = __state;
        }

        private static bool GroundBelow(Character character)
        {
            float radius = Body(character).radius * 0.9f;
            var from = character.transform.position + Vector3.up * (radius + 0.1f);
            return Physics.SphereCast(from, radius, Vector3.down, out _, GroundRange + 0.1f, (int)GroundMask.GetValue(null), QueryTriggerInteraction.Ignore);
        }
    }
}
