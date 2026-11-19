using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The Wisp pushes mist out of its bubble with a fixed strength made for an ordinary sprint. A faster player
    // carries the bubble over mist sooner than it is pushed out, so the push grows with the speed past that sprint.
    [HarmonyPatch(typeof(SE_Demister), nameof(SE_Demister.UpdateStatusEffect))]
    internal static class WispSpeedPush
    {
        private static ParticleSystemForceField field;
        private static float baseGravity;
        private static float factor = 1;

        private static void Postfix(GameObject ___m_ballInstance, Character ___m_character)
        {
            if (!___m_ballInstance || ___m_character != Player.m_localPlayer) return;
            var ball = ___m_ballInstance.GetComponentInChildren<ParticleSystemForceField>();
            if (!ball) return;
            if (ball != field)
            {
                field = ball;
                baseGravity = ball.gravity.constant;
                factor = 1;
            }
            var velocity = ___m_character.GetVelocity();
            velocity.y = 0;
            // The push is an acceleration, so mist the bubble moves onto goes as deep before it turns back as the
            // square of the speed: the push grows with that square to keep it as shallow as at an ordinary sprint.
            // The ratio goes in steps of 5%, so the field is set only when the speed changes noticeably.
            float ratio = Mathf.Max(1, Mathf.Round(velocity.magnitude / ((Player)___m_character).m_runSpeed * 20) / 20);
            float wanted = ratio * ratio;
            if (wanted == factor) return;
            factor = wanted;
            field.gravity = baseGravity * factor;
        }
    }
}
