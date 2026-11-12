using System;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class SpawnerDamageProtection
    {
        private struct DamageScope
        {
            internal bool AreaDamage;
            internal Projectile Projectile;
            internal Destructible DirectHit;
        }

        [ThreadStatic] private static DamageScope currentScope;

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(SpawnerDamageProtection);
            HarmonyMethod Method(string name) => new HarmonyMethod(self, name) { priority = Priority.First + 1 };
            harmony.Patch(AccessTools.Method(typeof(Destructible), nameof(Destructible.Damage)), prefix: Method(nameof(AllowSpawnerDamage)));
            harmony.Patch(AccessTools.Method(typeof(Aoe), "OnHit"), prefix: Method(nameof(BeginArea)), finalizer: Method(nameof(EndScope)));
            harmony.Patch(AccessTools.Method(typeof(Attack), "DoAreaAttack"), prefix: Method(nameof(BeginArea)), finalizer: Method(nameof(EndScope)));
            harmony.Patch(AccessTools.Method(typeof(OverwhelmingLaunch), "DoSlam"), prefix: Method(nameof(BeginArea)), finalizer: Method(nameof(EndScope)));
            harmony.Patch(AccessTools.Method(typeof(Projectile), "OnHit"), prefix: Method(nameof(BeginProjectile)), finalizer: Method(nameof(EndScope)));
            harmony.Patch(AccessTools.Method(typeof(Projectile), "DoAOE"), prefix: Method(nameof(BeginSplash)), finalizer: Method(nameof(EndScope)));
        }

        private static void BeginArea(out DamageScope? __state)
        {
            __state = currentScope;
            currentScope = new DamageScope { AreaDamage = true };
        }

        private static void BeginProjectile(Projectile __instance, Collider collider, out DamageScope? __state)
        {
            __state = currentScope;
            var target = collider ? Projectile.FindHitObject(collider) : null;
            currentScope = new DamageScope { Projectile = __instance, DirectHit = target ? target.GetComponent<Destructible>() : null };
        }

        private static void BeginSplash(Projectile __instance, out DamageScope? __state)
        {
            __state = currentScope;
            // Explosive projectiles apply their contact damage through DoAOE too. Only their actual impact target
            // may take that hit; a secondary Aoe starts its own scope without this exception.
            currentScope = new DamageScope { AreaDamage = true,
                DirectHit = currentScope.Projectile == __instance ? currentScope.DirectHit : null };
        }

        private static void EndScope(DamageScope? __state)
        {
            if (__state.HasValue) currentScope = __state.Value;
        }

        private static bool AllowSpawnerDamage(Destructible __instance)
        {
            // Reject area damage before Damage sends its ownership RPC. Direct hits retain native resistances,
            // regardless of attacker, and no source record is needed after an effect's owner disappears.
            return !currentScope.AreaDamage || currentScope.DirectHit == __instance
                || __instance.GetComponentInChildren<SpawnArea>(true) == null;
        }
    }
}
