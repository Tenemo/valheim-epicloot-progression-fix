using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class CreatureSizeCompatibility
    {
        private static PropertyInfo creatureSettings;
        private static PropertyInfo maximumSize;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var sizing = StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.Sizes.SizeModifications", true);
            var maximum = AccessTools.Method(sizing, "MaxScaleMultiplier", new[] { StarLevelSystem.CharacterCacheEntry });
            creatureSettings = AccessTools.Property(StarLevelSystem.CharacterCacheEntry, "CreatureSettings");
            maximumSize = AccessTools.Property(creatureSettings?.PropertyType, "MaxSizeScale");
            if (maximum?.ReturnType != typeof(float) || maximumSize?.PropertyType != typeof(float))
                throw new InvalidOperationException("StarLevelSystem's creature size limit changed.");
            harmony.Patch(maximum, postfix: new HarmonyMethod(typeof(CreatureSizeCompatibility), nameof(PreserveGlobalSizing)));
            var mountScaling = StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.Sizes.MountScaling", true);
            harmony.Patch(AccessTools.Method(mountScaling, "AimAtSaddle") ?? throw new MissingMethodException(mountScaling.FullName, "AimAtSaddle"),
                prefix: new HarmonyMethod(typeof(CreatureSizeCompatibility), nameof(KeepBareAnimalHover)));
            var firstHit = AccessTools.Method(mountScaling, "FirstHoverHit");
            if (firstHit?.ReturnType != typeof(bool) || firstHit.GetParameters().Length != 2 || firstHit.GetParameters()[1].ParameterType != typeof(RaycastHit).MakeByRefType())
                throw new InvalidOperationException("StarLevelSystem's saddle aim changed.");
            harmony.Patch(firstHit, postfix: new HarmonyMethod(typeof(CreatureSizeCompatibility), nameof(BodyBehindContinuousBody)));
        }

        // A big mount's upper body offers its saddle. On a Spirit Caller animal that would let anyone ride it and offer to remove
        // a saddle it doesn't have, so it keeps its own prompts, where only the caster's Use rides.
        private static bool KeepBareAnimalHover(Character creature) => !SpiritMounts.Bare(creature.GetComponent<Tameable>());

        private static readonly List<Collider> bodyColliders = new List<Collider>();

        // StarLevelSystem offers the saddle where the aim meets the body at half the seat's height or above. A big moose's
        // continuous body fills the groove under its upper body, so from beside it the aim meets that fill below half the seat
        // and the saddle stays hidden. The offer judges where the aim meets the moose's own capsules behind it instead.
        private static void BodyBehindContinuousBody(ref RaycastHit __1, bool __result)
        {
            var hull = __1.collider;
            if (!__result || !MooseBody.IsContinuousBody(hull) || !GameCamera.instance) return;
            var camera = GameCamera.instance.transform;
            var aim = new Ray(camera.position, camera.forward);
            hull.transform.parent.GetComponentsInChildren(bodyColliders);
            float nearest = float.PositiveInfinity;
            foreach (var collider in bodyColliders)
                if (collider != hull && collider.enabled && !collider.isTrigger && collider.gameObject.layer == hull.gameObject.layer &&
                    collider.Raycast(aim, out var behind, 50f) && behind.distance < nearest)
                {
                    nearest = behind.distance;
                    __1 = behind;
                }
            bodyColliders.Clear();
        }

        private static void PreserveGlobalSizing(object __0, ref float __result)
        {
            var settings = __0 == null ? null : creatureSettings.GetValue(__0);
            if (settings == null || (float)maximumSize.GetValue(settings) <= 0f) __result = float.PositiveInfinity;
        }
    }
}
