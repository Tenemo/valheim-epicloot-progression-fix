using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static partial class SleepEnemyPause
    {
        private static readonly HashSet<GameObject> EnemyVisuals = new();
        private static Type earthCallerType, shieldType, spikeVisualType, spikeRiseType;

        private static bool EnemyComponent(Component component)
        {
            if (!component) return false;
            if (EnemyVisuals.Contains(component.gameObject)) return true;
            var projectile = component.GetComponentInParent<Projectile>();
            if (projectile) return EnemyAttack(projectile);
            var area = component.GetComponentInParent<Aoe>();
            if (area) return EnemyAttack(area);
            return Hostile(component.GetComponentInParent<Character>());
        }

        private static void InitializeModifierPauses(IntegrationPatches patches)
        {
            var assembly = StarLevelSystem.Assembly;
            earthCallerType = assembly.GetType("StarLevelSystem.Modifiers.SLSEarthCaller", true);
            shieldType = assembly.GetType("StarLevelSystem.Modifiers.SLSShield", true);
            spikeVisualType = assembly.GetType("StarLevelSystem.Modifiers.EarthSpikeVisual", true);
            spikeRiseType = assembly.GetType("StarLevelSystem.Modifiers.EarthSpikeRise", true);
            var producer = new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowSpawnProcedure)) { priority = Priority.First };
            foreach (var entry in new[] { (earthCallerType, "Tick"), (earthCallerType, "Cast"), (shieldType, "Update"), (shieldType, "Tick") })
                patches.Patch(AccessTools.Method(entry.Item1, entry.Item2), prefix: producer);
            patches.Patch(AccessTools.Method(earthCallerType, "Configure"), transpiler: new HarmonyMethod(typeof(SleepEnemyPause), nameof(TrackSpawnInvokes)));
            foreach (var entry in new[] { (spikeVisualType, "Update"), (spikeVisualType, "Erupt"), (spikeRiseType, "Update") })
                patches.Patch(AccessTools.Method(entry.Item1, entry.Item2),
                    prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowModifierVisual)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(SleepEnemyPause), nameof(EndAttack)));
            patches.Patch(AccessTools.Method(spikeVisualType, "Raise"), transpiler: new HarmonyMethod(typeof(SleepEnemyPause), nameof(SpikeExpiry)));
            var reflection = AccessTools.Method(assembly.GetType("StarLevelSystem.Modifiers.Reflection+ReflectProjectile", true), "Prefix");
            patches.Patch(reflection, prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowReflection)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(ReflectedOwner)));
            patches.Patch(AccessTools.Method(assembly.GetType("StarLevelSystem.Modifiers.DeathNova+DeathNovaOnDeathPatch", true), "Prefix"),
                prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(BeginDeathAttack)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(SleepEnemyPause), nameof(EndAttack)));
        }

        private static bool BeginDeathAttack(Character __0, out Emission __state)
        {
            BeginEmission(Hostile(__0), out __state);
            return !Paused || !EnemyEmission();
        }

        private static bool AllowReflection(Collider __1, ref bool __result)
        {
            if (!Paused || !__1) return true;
            var target = Projectile.FindHitObject(__1);
            if (!target || !Hostile(target.GetComponent<Character>())) return true;
            __result = true;
            return false;
        }

        private static void ReflectedOwner(Projectile __0) { if (__0) RememberAttack(__0, ProjectileOwner(__0)); }

        private static bool AllowModifierVisual(MonoBehaviour __instance, out Emission __state)
        {
            BeginEmission(EnemyComponent(__instance), out __state);
            if (!Paused || !EnemyEmission()) return true;
            Freeze(__instance.gameObject);
            return false;
        }

        private static IEnumerable<CodeInstruction> SpikeExpiry(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var destroy = AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy), new[] { typeof(UnityEngine.Object), typeof(float) });
            var calls = code.Where(instruction => instruction.Calls(destroy)).ToArray();
            if (calls.Length != 1) throw new InvalidOperationException("StarLevelSystem's ground-spike lifetime changed.");
            calls[0].opcode = OpCodes.Call; calls[0].operand = AccessTools.Method(typeof(SleepEnemyPause), nameof(ExpireAttackVisual));
            return code;
        }

        private static void ExpireAttackVisual(UnityEngine.Object target, float delay)
        {
            if (!EnemyEmission() || !(target is GameObject instance)) { UnityEngine.Object.Destroy(target, delay); return; }
            EnemyVisuals.Add(instance);
            runner.StartCoroutine(ExpireVisual(instance, delay, ZNet.instance));
        }

        private static IEnumerator ExpireVisual(GameObject instance, float remaining, ZNet network)
        {
            try
            {
                while (instance && network == ZNet.instance && remaining > 0f)
                {
                    yield return null;
                    if (!Paused) remaining -= Time.deltaTime;
                }
                if (instance) UnityEngine.Object.Destroy(instance);
            }
            finally { EnemyVisuals.Remove(instance); }
        }

        private static void CaptureModifierClocks()
        {
            foreach (var spec in new[] { (earthCallerType, "nextCast"), (shieldType, "nextTick"), (spikeVisualType, "eruptAt"), (spikeRiseType, "start") })
                foreach (MonoBehaviour instance in UnityEngine.Object.FindObjectsByType(spec.Item1, FindObjectsSortMode.None))
                    if (EnemyComponent(instance)) RememberNumber(instance, AccessTools.Field(spec.Item1, spec.Item2), false);
            foreach (MonoBehaviour shield in UnityEngine.Object.FindObjectsByType(shieldType, FindObjectsSortMode.None))
            {
                if (!EnemyComponent(shield)) continue;
                var lastHit = AccessTools.Field(shieldType, "lastHit");
                ModClocks.Add((world, game) =>
                {
                    if (!shield) return;
                    float value = (float)lastHit.GetValue(shield);
                    lastHit.SetValue(shield, value + Mathf.Min(game, Mathf.Max(0f, Time.time - value)));
                });
            }
        }

        private static void ClearModifierClocks()
        {
            foreach (var visual in EnemyVisuals.ToArray()) if (visual) UnityEngine.Object.Destroy(visual);
            EnemyVisuals.Clear();
        }
    }
}
