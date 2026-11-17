using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A bow shot is one cycle: the release animation, then the draw. The game started the next draw the moment the button
    // went down, while the previous release was still playing, so from about Bows 85 the crosshair closed before the bow
    // could fire, and a release in that time was refused, or held back until the bow was ready and left fuller than the
    // circle showed. Now the draw starts only once the release has ended, the release plays twice as fast and the draw is
    // shorter by about the time it takes, so a bow fires as many arrows a second as before at every Bows level and the
    // circle always shows the draw the arrow leaves with. Fire rate and Attack speed speed up the whole cycle, draw
    // included; Quick Draw and Sniper change only the draw, Quick Draw as much as before up to the shortest draw.
    internal static class BowCycle
    {
        private const float ReleaseSpeed = 2f;
        // Quick Draw shortens the draw as before, then it is Overlap shorter, but no shorter than ShortestDraw with Quick Draw,
        // and Fire rate and Attack speed shorten the result: rapid fire then keeps the game's own pace at every Bows level,
        // within a physics step.
        private const float Overlap = .30f, ShortestDraw = .46f;
        // A release that never reaches its animation stops holding the next draw back after this long.
        private const float LongestRelease = 1f;
        // Seconds after a release in which the bow's turns back to aim are sped up.
        private const float AimWindow = .5f;
        private static float aimBy = -1f;
        private static readonly AccessTools.FieldRef<Humanoid, float> DrawTime = AccessTools.FieldRefAccess<Humanoid, float>("m_attackDrawTime");
        private static readonly AccessTools.FieldRef<Attack, float> AttackTime = AccessTools.FieldRefAccess<Attack, float>("m_time");

        private static bool IsBow(ItemDrop.ItemData weapon) => weapon != null && weapon.m_shared.m_attack.m_bowDraw &&
            weapon.m_shared.m_skillType == Skills.SkillType.Bows && weapon.m_shared.m_name != "$item_graplinghook";

        // Fire rate and Attack speed, as EpicLoot applies them to the release animation.
        private static float Speed(Player player)
        {
            float speed = 1f + player.GetTotalActiveMagicEffectValue(MagicEffectType.ModifyFireRate, .01f);
            ModifyWithLowHealth.Apply(player, MagicEffectType.ModifyAttackSpeed, effect => speed *= 1f + player.GetTotalActiveMagicEffectValue(effect, .01f));
            return speed;
        }

        private static bool Releasing(Player player)
        {
            var attack = GameFields.CurrentAttack(player);
            return player.InAttack() || attack != null && !attack.IsDone() && AttackTime(attack) < LongestRelease || Time.time < AttackCancel.AttackAllowedAt;
        }

        // A held button starts the draw on the first step after the release; one let go before then draws nothing.
        [HarmonyPatch(typeof(Player), "UpdateAttackBowDraw")]
        private static class DrawAfterRelease
        {
            private static bool wasReleasing;

            private static bool Prefix(Player __instance, ItemDrop.ItemData weapon)
            {
                if (!IsBow(weapon)) return true;
                bool releasing = Releasing(__instance);
                if (wasReleasing && !releasing) aimBy = Time.time + AimWindow;
                wasReleasing = releasing;
                return !(releasing && DrawTime(__instance) == 0f && GameFields.AttackHold(__instance) && !GameFields.Blocking(__instance) &&
                    !__instance.InMinorAction() && !__instance.IsAttached());
            }
        }

        // The release ends by going back through the idle pose to aim, which at normal speed takes longer than a short draw,
        // and a shot waits for the aim. While the next draw is held, those turns play at the release's own speed, as the
        // owner's animation speed reaches everyone.
        [HarmonyPatch(typeof(CharacterAnimEvent), nameof(CharacterAnimEvent.CustomFixedUpdate))]
        private static class QuickerAim
        {
            private static bool sped;

            // Last, after the game has reset the speed outside attacks and EpicLoot has applied its own.
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Character ___m_character, Animator ___m_animator)
            {
                if (!ReferenceEquals(___m_character, Player.m_localPlayer) || !___m_animator) return;
                var player = Player.m_localPlayer;
                if (Time.time <= aimBy && !player.InAttack() && ___m_animator.IsInTransition(0) && IsBow(player.GetCurrentWeapon()) &&
                    (GameFields.AttackHold(player) || DrawTime(player) > 0f))
                {
                    ___m_animator.speed = ReleaseSpeed * Speed(player);
                    sped = true;
                }
                else if (sped)
                {
                    // The game resets it only while the character can move.
                    sped = false;
                    if (!player.InAttack()) ___m_animator.speed = 1f;
                }
            }
        }

        // While Sniper has lengthened the bow's draw, and in place of EpicLoot's Quick Draw, which multiplied the result.
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.GetAttackDrawPercentage))]
        private static class ShorterDraw
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Humanoid __instance, ref float __result)
            {
                if (!(__result > 0f) || !(__instance is Player player)) return;
                var weapon = player.GetCurrentWeapon();
                if (!IsBow(weapon)) return;
                float baseDraw = weapon.m_shared.m_attack.m_drawDurationMin;
                float full = Mathf.Lerp(baseDraw, baseDraw * .2f, player.GetSkillFactor(weapon.m_shared.m_skillType));
                if (!(full > 0f)) return;
                float quick = player.HasActiveMagicEffect(MagicEffectType.QuickDraw, out float value, .01f) ? 1f + value : 1f;
                float draw = Mathf.Min(full, Mathf.Max(full - Overlap * quick, ShortestDraw)) / quick;
                __result = Mathf.Clamp01(DrawTime(player) * Speed(player) / draw);
            }
        }

        // The owner's animation speed reaches the other players' games with the rest of the animation state.
        [HarmonyPatch(typeof(ModifyMagicFireRate), nameof(ModifyMagicFireRate.ModifyFireAnimationSpeed))]
        private static class FasterRelease
        {
            private static void Postfix(Character character, ref double __result)
            {
                if (character == Player.m_localPlayer && character.InAttack() && IsBow(GameFields.CurrentAttack((Player)character)?.GetWeapon()))
                    __result *= ReleaseSpeed;
            }
        }
    }
}
