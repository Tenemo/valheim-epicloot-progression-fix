using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using EpicLoot.MagicItemEffects.Shards;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The Yagluth meteor and Eikthyr's charge are bonus hits that take a share of the damage of the hit that triggers
    // them. EpicLoot reads that damage after crit and double damage have multiplied the hit, so a crit multiplied them
    // too. Give them the hit before those multipliers.
    internal static class BonusHits
    {
        // Crit and double damage multiply the whole hit. Remember by how much, so the meteor and Eikthyr's charge can
        // build on the hit before them.
        [HarmonyPatch]
        private static class StrikeMultipliers
        {
            internal static readonly ConditionalWeakTable<HitData, StrongBox<float>> Factors = new ConditionalWeakTable<HitData, StrongBox<float>>();

            private static IEnumerable<MethodBase> TargetMethods() => Required(
                AccessTools.Method(typeof(ChanceToCritOnHit), nameof(ChanceToCritOnHit.ModifyOutgoingHit)),
                AccessTools.Method(typeof(ChanceDoubleDamage), nameof(ChanceDoubleDamage.ModifyOutgoingHit)));

            private static void Prefix(HitData hit, out float __state) => __state = hit == null ? 0f : hit.GetTotalDamage();

            private static void Postfix(HitData hit, float __state)
            {
                if (hit == null || !(__state > 0f)) return;
                float after = hit.GetTotalDamage();
                if (after == __state || !(after > 0f)) return;
                if (!Factors.TryGetValue(hit, out var factor))
                {
                    factor = new StrongBox<float>(1f);
                    Factors.Add(hit, factor);
                }
                factor.Value *= after / __state;
            }
        }

        // The meteor and Eikthyr's charge take a share of the hit's damage; give them the hit before crit and double
        // damage, and put the full hit back for the effects after them.
        [HarmonyPatch]
        private static class BeforeStrikeMultipliers
        {
            private static IEnumerable<MethodBase> TargetMethods() => Required(
                AccessTools.Method(typeof(MeteorSummoner), nameof(MeteorSummoner.OnDamageDealt)),
                AccessTools.Method(typeof(EikthyrShockingCharge), nameof(EikthyrShockingCharge.OnDamageDealt)));

            private static void Prefix(HitData hit, out HitData.DamageTypes? __state)
            {
                __state = null;
                if (hit == null || !StrikeMultipliers.Factors.TryGetValue(hit, out var factor) || !(factor.Value > 0f)) return;
                __state = hit.m_damage;
                hit.m_damage.Modify(1f / factor.Value);
            }

            private static void Finalizer(HitData hit, HitData.DamageTypes? __state)
            {
                if (hit != null && __state.HasValue) hit.m_damage = __state.Value;
            }
        }

        private static IEnumerable<MethodBase> Required(params MethodBase[] methods)
        {
            if (methods.Any(method => method == null)) throw new InvalidOperationException("EpicLoot's crit, double damage, meteor or charge moved.");
            return methods;
        }
    }
}
