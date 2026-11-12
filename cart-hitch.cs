using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The game hangs a pulled cart's shafts from a soft spring at the puller's hip. Run skill and movement speed gear let a
    // player sprint at 12 to 17 m/s, twice an unskilled sprint, and on uneven ground the spring then lets the nose drop
    // 20 to 60 degrees, the shafts and bed drag on the ground and the cart can come loose. A hitch four times as stiff,
    // with twice the damping, keeps the shafts near the hip at those speeds.
    [HarmonyPatch(typeof(Vagon), "AttachTo")]
    internal static class CartHitch
    {
        private const float SpringFactor = 4f, DamperFactor = 2f;
        private static readonly AccessTools.FieldRef<Vagon, ConfigurableJoint> Joint = AccessTools.FieldRefAccess<Vagon, ConfigurableJoint>("m_attachJoin");

        private static void Postfix(Vagon __instance)
        {
            var joint = Joint(__instance);
            if (!joint) return;
            var spring = joint.linearLimitSpring;
            spring.spring = __instance.m_spring * SpringFactor;
            spring.damper = __instance.m_springDamping * DamperFactor;
            joint.linearLimitSpring = spring;
        }
    }
}
