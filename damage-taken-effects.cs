using System;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot's damage-taken effects leave out hits that never land and hits a character deals itself, but still
    // react to falls, spending coins and eitr and starting cooldowns on them. Leave falls alone: the effects as the
    // hit arrives (night damage reduction, OffSet, Feint and Reflect), their follow-ups (adrenaline, Bloodrage,
    // Forest's Aid and Icy Retribution), those after armor (Eitr Shield and Coinplated) and Eitr Barrier. Lava and
    // ocean ticks stay with the barrier.
    internal static class DamageTakenEffects
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            var assembly = typeof(ModifyDamage).Assembly;
            var arriving = AccessTools.Method(assembly.GetType("EpicLoot.src.Magic.MagicItemEffects.Helpers.SharedCharacterRpcDamagePatch"), "Prefix");
            var afterArmor = AccessTools.Method(assembly.GetType("EpicLoot.src.Magic.MagicItemEffects.Helpers.SharedPlayerPostArmorDamagePatch"), "Postfix");
            if (arriving == null || afterArmor == null) throw new InvalidOperationException("EpicLoot's damage-taken effects moved.");
            harmony.Patch(arriving, prefix: new HarmonyMethod(typeof(DamageTakenEffects), nameof(SkipFall)));
            harmony.Patch(afterArmor, prefix: new HarmonyMethod(typeof(DamageTakenEffects), nameof(NotForFalls)));
            harmony.Patch(AccessTools.Method(typeof(EitrBarrier), "Absorb"), prefix: new HarmonyMethod(typeof(DamageTakenEffects), nameof(NotForFalls)));
        }

        // EpicLoot's own state for the hit stays unset, so its follow-up effects skip the fall as well.
        private static bool SkipFall(HitData hit, ref bool __result)
        {
            if (hit == null || hit.m_hitType != HitData.HitType.Fall) return true;
            __result = true;
            return false;
        }

        private static bool NotForFalls(HitData hit) => hit == null || hit.m_hitType != HitData.HitType.Fall;
    }
}
