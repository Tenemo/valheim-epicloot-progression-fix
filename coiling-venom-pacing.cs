using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Coiling Venom adds each hit's converted poison to what is left of the poison, and the game spreads a poison over
    // 2 + sqrt(2 x remaining) seconds, so the converted damage kept stretching out: 1,000 pending poison took 47 s and most
    // of it landed after the fight or never. A poison Coiling Venom tops up lands over a fixed three seconds instead.
    internal static class CoilingVenomPacing
    {
        private const int Ticks = 3;

        private static readonly AccessTools.FieldRef<SE_Poison, float> DamagePerHit = AccessTools.FieldRefAccess<SE_Poison, float>("m_damagePerHit");
        private static readonly AccessTools.FieldRef<SE_Poison, float> Timer = AccessTools.FieldRefAccess<SE_Poison, float>("m_timer");
        private static Type venom;
        private static FieldInfo armed;

        internal static void Initialize(IntegrationPatches harmony)
        {
            venom = AccessTools.TypeByName("EpicLoot.MagicItemEffects.CoiledVenom") ?? throw new MissingMemberException("EpicLoot.MagicItemEffects.CoiledVenom");
            armed = AccessTools.Field(venom, "Armed") ?? throw new MissingFieldException(venom.FullName, "Armed");
            // First, so the venom state is read before EpicLoot's own prefix disarms it.
            harmony.Patch(AccessTools.Method(typeof(Character), "AddPoisonDamage"),
                prefix: new HarmonyMethod(typeof(CoilingVenomPacing), nameof(ReadArmed)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(CoilingVenomPacing), nameof(Pace)));
        }

        private static void ReadArmed(Character __instance, out bool __state)
        {
            var component = __instance.GetComponent(venom);
            __state = component && (bool)armed.GetValue(component);
        }

        private static void Pace(Character __instance, bool __state)
        {
            if (!__state || !(__instance.GetSEMan().GetStatusEffect(SEMan.s_statusEffectPoison) is SE_Poison poison) || GameFields.EffectTime(poison) != 0f) return;
            // The next tick keeps its time, so hits that come faster than ticks never hold them back. Ticks fall one interval
            // apart from it and the poison ends half an interval after the last, so exactly Ticks land.
            float next = Mathf.Clamp(Timer(poison), 0f, poison.m_damageInterval);
            poison.m_ttl = next + (Ticks - .5f) * poison.m_damageInterval;
            DamagePerHit(poison) = GameFields.PoisonLeft(poison) / Ticks;
            Timer(poison) = next;
        }
    }
}
