using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A bow fires again only once the previous shot's animation ends, about 0.8 seconds after the shot, but the next
    // draw and its crosshair fill from the moment the button goes down. From Bows skill 85, or lower with Quick Draw or
    // a Bows skill bonus, the draw is full before then, and the game drops a release in that time: no arrow, no
    // ammunition used and no message. Keep such an arrow drawn until the bow can fire and loose it then, even if the
    // button is pressed again meanwhile, as the game already keeps a melee click made during an attack. A release
    // refused for another reason, such as a dodge or no stamina, is dropped as before.
    [HarmonyPatch(typeof(Player), "UpdateAttackBowDraw")]
    internal static class BowRelease
    {
        // The previous shot's animation ends within 0.8 seconds of it; any longer means another animation is playing.
        private const float MaxWait = 1f;
        private static readonly AccessTools.FieldRef<Humanoid, float> DrawTime = AccessTools.FieldRefAccess<Humanoid, float>("m_attackDrawTime");
        // Game time by which a held-back release is let go, or -1 while none is held back.
        private static float releaseBy = -1f;

        private struct DrawState
        {
            internal bool AttackHeld;
            internal bool QueuedRelease;
            internal Attack PreviousAttack;
        }

        private static void Prefix(Player __instance, out DrawState __state)
        {
            __state = new DrawState { AttackHeld = GameFields.AttackHold(__instance) };
            if (__instance != Player.m_localPlayer) return;
            if (DrawTime(__instance) <= 0f)
            {
                // The draw was fired, blocked or put away.
                releaseBy = -1f;
                return;
            }
            bool heldBack = releaseBy >= 0f;
            if (!heldBack && __state.AttackHeld) return;
            // An attack abandoned by raising the guard holds a release back for as long as it delays the next attack.
            if ((__instance.InAttack() || Time.time < AttackCancel.AttackAllowedAt) && (!heldBack || Time.time < releaseBy))
            {
                if (!heldBack) releaseBy = Time.time + MaxWait;
                GameFields.AttackHold(__instance) = true;
                return;
            }
            releaseBy = -1f;
            GameFields.AttackHold(__instance) = false;
            __state.QueuedRelease = heldBack;
            __state.PreviousAttack = GameFields.CurrentAttack(__instance);
        }

        // The button's real state, which the game sets again from input on every step.
        private static void Postfix(Player __instance, DrawState __state)
        {
            GameFields.AttackHold(__instance) = __state.AttackHeld;
            if (!__state.QueuedRelease) return;
            var attack = GameFields.CurrentAttack(__instance);
            if (attack != null && attack != __state.PreviousAttack && attack.GetWeapon()?.m_shared.m_skillType == Skills.SkillType.Bows)
                QueuedBowCancellation.Track(__instance, attack);
        }
    }
}
