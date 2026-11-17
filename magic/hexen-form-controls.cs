using System;
using EpicLoot.Transformation;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The Jotun Witch form lasts while its staff is in hand and takes the Jump control for flight. Eating or pressing Hide
    // stowed the staff, which ended the form and dropped a flying witch out of the sky. Jump took off from chairs, helms and
    // saddles instead of leaving them, did nothing on the ground without the eitr to take off, and flight never ended on
    // water, where the drain then stopped eitr coming back. Ending the form turned the hidden body back on, which restarted
    // its animator in a cutscene state that for almost 3 s took the controls, ignored hits and closed the inventory.
    internal static class HexenFormControls
    {
        private static readonly Func<HexenForm, bool> CanTakeOff =
            AccessTools.MethodDelegate<Func<HexenForm, bool>>(AccessTools.PropertyGetter(typeof(HexenForm), "CanTakeOff"));
        private static readonly AccessTools.FieldRef<Character, Animator> BodyAnimator = AccessTools.FieldRefAccess<Character, Animator>("m_animator");
        private static bool jumpHeldBack;
        private static int leavingFrame = -1;

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(HexenFormControls);
            if (AccessTools.Inner(typeof(HexenForm), "Player_SetControls_Patch") == null)
                throw new InvalidOperationException("EpicLoot's Hexen flight control moved.");
            var controls = AccessTools.Method(typeof(Player), nameof(Player.SetControls));
            harmony.Patch(AccessTools.Method(typeof(HexenForm), "Apply"), prefix: new HarmonyMethod(self, nameof(KeepBodyAnimation)));
            harmony.Patch(AccessTools.Method(typeof(Humanoid), nameof(Humanoid.HideHandItems)), prefix: new HarmonyMethod(self, nameof(KeepStaffInHand)));
            harmony.Patch(controls, prefix: new HarmonyMethod(self, nameof(HoldJumpBack)) { priority = Priority.High });
            harmony.Patch(controls, prefix: new HarmonyMethod(self, nameof(RestoreJump)) { priority = Priority.Low });
            harmony.Patch(controls, postfix: new HarmonyMethod(self, nameof(LandOnWaterWithoutEitr)));
        }

        private static void KeepBodyAnimation(Player ____player) => BodyAnimator(____player).keepAnimatorStateOnDisable = true;

        // Eating hides only the right hand. Workbenches, beds and Hide on the ground hide both and still end the form.
        private static bool KeepStaffInHand(Humanoid __instance, bool onlyRightHand, ref bool __result)
        {
            if (__instance != Player.m_localPlayer || !HexenForm.TryGet(__instance, out var form) || !form.Active) return true;
            if (!onlyRightHand && !form.Flying) return true;
            __result = false;
            return false;
        }

        // EpicLoot's prefix between these two turns any Jump into flight. Every physics step of a frame reads the same press,
        // so once one step has left the seat the rest of that frame still counts as leaving it.
        private static void HoldJumpBack(Player __instance, ref bool jump)
        {
            jumpHeldBack = false;
            if (!jump || !HexenForm.TryGet(__instance, out var form) || !form.Active) return;
            if (__instance.IsAttached() || __instance.GetDoodadController() != null) leavingFrame = Time.frameCount;
            jumpHeldBack = leavingFrame == Time.frameCount || (!form.Flying && __instance.IsOnGround() && !__instance.IsSwimming() && !CanTakeOff(form));
            if (jumpHeldBack) jump = false;
        }

        private static void RestoreJump(ref bool jump)
        {
            if (!jumpHeldBack) return;
            jump = true;
            jumpHeldBack = false;
        }

        private static void LandOnWaterWithoutEitr(Player __instance)
        {
            if (HexenForm.TryGet(__instance, out var form) && form.Flying && __instance.IsSwimming() && __instance.GetEitr() <= 0f) __instance.Land();
        }
    }
}
