using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Quarry Mark asks for the set when an arrow lands, and the bow is one of its pieces, so swapping to the axe the mark is
    // meant for left an arrow still in flight marking nothing. An arrow now carries the mark from the moment it's fired.
    internal static class QuarryMarkInFlight
    {
        private static readonly ConditionalWeakTable<Projectile, object> Shots = new ConditionalWeakTable<Projectile, object>();
        private static object landing;

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(QuarryMarkInFlight);
            harmony.Patch(AccessTools.Method(typeof(Projectile), nameof(Projectile.Setup)), postfix: new HarmonyMethod(self, nameof(RememberMark)));
            foreach (var method in new[] { AccessTools.Method(typeof(Projectile), "DoAOE"), AccessTools.Method(typeof(Projectile), nameof(Projectile.OnHit)) })
                harmony.Patch(method, prefix: new HarmonyMethod(self, nameof(BeginLanding)), finalizer: new HarmonyMethod(self, nameof(EndLanding)));
            harmony.Patch(AccessTools.Method(typeof(QuarryMark), nameof(QuarryMark.OnDamageDealt)), transpiler: new HarmonyMethod(self, nameof(ReadMarkFromShot)));
        }

        private static void RememberMark(Projectile __instance, Character owner, ItemDrop.ItemData item)
        {
            if (owner is Player player && player == Player.m_localPlayer && ElementalArchery.IsDrawnBow(item) && player.HasActiveMagicEffect(MagicEffectType.QuarryMark, out float value))
                Shots.Add(__instance, value);
        }

        private static void BeginLanding(Projectile __instance, out object __state)
        {
            __state = landing;
            landing = Shots.TryGetValue(__instance, out object value) ? value : null;
        }

        private static Exception EndLanding(Exception __exception, object __state)
        {
            landing = __state;
            return __exception;
        }

        private static IEnumerable<CodeInstruction> ReadMarkFromShot(IEnumerable<CodeInstruction> instructions)
        {
            var active = AccessTools.Method(typeof(global::EpicLoot.PlayerExtensions), "HasActiveMagicEffect",
                new[] { typeof(Player), typeof(string), typeof(float).MakeByRefType(), typeof(float), typeof(ItemDrop.ItemData) });
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(active))
                {
                    instruction.operand = AccessTools.Method(typeof(QuarryMarkInFlight), nameof(MarkValue));
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1) throw new InvalidOperationException("EpicLoot's Quarry Mark no longer reads its effect once.");
        }

        private static bool MarkValue(Player player, string effect, out float value, float scale, ItemDrop.ItemData ignored)
        {
            if (landing == null) return player.HasActiveMagicEffect(effect, out value, scale, ignored);
            value = (float)landing;
            return true;
        }
    }
}
