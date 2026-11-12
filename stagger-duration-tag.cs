using System.Collections.Generic;
using System.Reflection;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot tags a target with the attacker's stagger duration just before a hit lands, and the target's stagger animation
    // reads that tag. Melee hits and blocks leave a target that is already staggering alone, but arrows, tagged from the
    // projectile when it hits, did not: one landing during another hit's stagger replaced the tag, which also stretched the
    // rest of a stagger that started without a bonus. Every tag now skips a staggering target, on the shooter's game and, for
    // a tag already on its way, on the owner's.
    [HarmonyPatch]
    internal static class StaggerDurationTag
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ModifyStaggerDuration), nameof(ModifyStaggerDuration.TagTarget));
            yield return AccessTools.Method(typeof(ModifyStaggerDuration), "RPC_TagStaggerDuration");
        }

        private static bool Prefix(Character __0) => __0 == null || !__0.IsStaggering();
    }
}
