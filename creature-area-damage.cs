using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // StarLevelSystem scales a creature's damage by its stars through the game's per-level attack factor, which only a
    // creature's strikes, area attacks and fired projectiles use. Area effects and projectiles with damage of their own
    // never read it, although SLS's elemental affixes still add to their hits: those an attack spawns, such as a boss's
    // lingering clouds or a shaman's fire, the explosion a creature leaves when it blows itself up or dies, Fader's
    // fissures and walls of fire, and effects built into a creature, such as a frost wisp's aura or a lox's trampling.
    // Give them the creature's SLS damage factor, stars and saved damage multiplier together, as its other attacks get,
    // on each hit as the game builds it; the effect's own damage stays as it was. A built-in effect takes the creature's
    // current factor, because the game sets it up before SLS gives the creature its stars. A lingering effect damages
    // each player through that player's own copy, which the game never sets up, so the factor also goes into the
    // effect's synced data for those copies. An effect set up after its creature is gone, for example by a projectile
    // landing after that creature died or unloaded, stays unscaled.
    internal static class CreatureAreaDamage
    {
        private static readonly int FactorKey = "epicloot_progression_creature_damage_factor".GetStableHashCode();
        private static readonly AccessTools.FieldRef<SpawnAbility, Character> SpawnerOf = AccessTools.FieldRefAccess<SpawnAbility, Character>("m_owner");
        // The factor each area effect's hits take.
        private static readonly ConditionalWeakTable<Aoe, StrongBox<float>> Factors = new ConditionalWeakTable<Aoe, StrongBox<float>>();
        private static Func<Character, float> factorOf;
        // The factor of the creature whose death or area attack is creating its effects, 1 otherwise.
        private static float creating = 1f;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var hit = AccessTools.Method(typeof(Aoe), "OnHit");
            var areaAttack = AccessTools.Method(typeof(Attack), "DoAreaAttack");
            var spawn = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(SpawnAbility), "Spawn"));
            if (hit == null || areaAttack == null || spawn == null)
                throw new InvalidOperationException("The game's area effects changed.");
            // Calls through the SoulEater cap's postfix on the same method.
            factorOf = AccessTools.MethodDelegate<Func<Character, float>>(StarLevelSystem.DetermineDamageFactor);
            var self = typeof(CreatureAreaDamage);
            harmony.Patch(AccessTools.Method(typeof(Aoe), nameof(Aoe.Setup)), postfix: new HarmonyMethod(self, nameof(RememberArea)));
            harmony.Patch(hit, transpiler: new HarmonyMethod(self, nameof(ScaleHits)));
            harmony.Patch(AccessTools.Method(typeof(Projectile), nameof(Projectile.Setup)), postfix: new HarmonyMethod(self, nameof(ScaleProjectile)));
            // First, so that no other patch runs before the factor is set, and on a death before StarLevelSystem's own.
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.OnDeath)),
                prefix: new HarmonyMethod(self, nameof(BeginDeath)) { priority = Priority.First }, finalizer: new HarmonyMethod(self, nameof(EndCreating)));
            harmony.Patch(areaAttack,
                prefix: new HarmonyMethod(self, nameof(BeginAreaAttack)) { priority = Priority.First }, finalizer: new HarmonyMethod(self, nameof(EndCreating)));
            harmony.Patch(AccessTools.Method(typeof(EffectList), nameof(EffectList.Create)), postfix: new HarmonyMethod(self, nameof(RememberCreated)));
            harmony.Patch(spawn, transpiler: new HarmonyMethod(self, nameof(RememberSpawned)));
        }

        // An attack hands an effect its own hit only once that hit carries the factor.
        private static float FactorFor(Character owner, HitData hitData, bool usesHit) =>
            owner == null || owner.IsPlayer() || (hitData != null && usesHit) ? 1f : factorOf(owner);

        // The creature an effect is built into, which the game gives that effect as its synced object.
        private static Character PartOf(Aoe area)
        {
            var view = GameFields.AoeView(area);
            return view ? view.GetComponent<Character>() : null;
        }

        private static void Remember(Aoe area, float factor)
        {
            Factors.Remove(area);
            Factors.Add(area, new StrongBox<float>(factor));
            var view = GameFields.AoeView(area);
            if (factor != 1f && view && view.IsValid() && view.IsOwner()) view.GetZDO().Set(FactorKey, factor);
        }

        private static void RememberArea(Aoe __instance, Character owner, HitData hitData)
        {
            if (!PartOf(__instance)) Remember(__instance, FactorFor(owner, hitData, __instance.m_useAttackSettings));
        }

        // A hit takes its damage from the effect in one place, once the effect has decided to hit.
        private static IEnumerable<CodeInstruction> ScaleHits(IEnumerable<CodeInstruction> instructions)
        {
            var damage = AccessTools.Method(typeof(Aoe), "GetDamage", Type.EmptyTypes);
            var code = instructions.ToList();
            var damageAt = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(damage)).ToList();
            if (damage == null || damageAt.Count != 1)
                throw new InvalidOperationException("The game's area hits changed.");
            code.InsertRange(damageAt[0] + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CreatureAreaDamage), nameof(Scale))),
            });
            return code;
        }

        private static HitData.DamageTypes Scale(HitData.DamageTypes damage, Aoe area)
        {
            var creature = PartOf(area);
            float factor = creature ? (creature.IsPlayer() ? 1f : factorOf(creature)) : Factors.GetValue(area, Synced).Value;
            if (factor != 1f) damage.Modify(factor);
            return damage;
        }

        // Another player's copy of an effect, or the owner's copy after it reloaded.
        private static StrongBox<float> Synced(Aoe area)
        {
            var view = GameFields.AoeView(area);
            return new StrongBox<float>(view && view.IsValid() ? view.GetZDO().GetFloat(FactorKey, 1f) : 1f);
        }

        // Projectiles deal their damage only on the game that fired them, so their own damage can carry the factor.
        private static void ScaleProjectile(Projectile __instance, Character owner, HitData hitData)
        {
            float factor = FactorFor(owner, hitData, true);
            if (factor != 1f) __instance.m_damage.Modify(factor);
        }

        // Only the creature's owner creates its death effects.
        private static void BeginDeath(Character __instance, out float? __state)
        {
            __state = creating;
            creating = __instance.IsPlayer() || !__instance.IsOwner() ? 1f : factorOf(__instance);
        }

        // A creature that blows itself up, such as a lava blob, creates its explosion from its weapon's effects.
        private static void BeginAreaAttack(Attack __instance, out float? __state)
        {
            __state = creating;
            var attacker = GameFields.AttackCharacter(__instance);
            creating = !attacker || attacker.IsPlayer() ? 1f : factorOf(attacker);
        }

        // Only after this patch's own prefix ran; another patch's prefix can fail before it.
        private static void EndCreating(float? __state)
        {
            if (__state.HasValue) creating = __state.Value;
        }

        // Effects are created with no owner and never set up; an explosion is the created object itself.
        private static void RememberCreated(GameObject[] __result)
        {
            if (creating == 1f || __result == null) return;
            foreach (var created in __result)
            {
                var area = created ? created.GetComponent<Aoe>() : null;
                if (area) Remember(area, creating);
            }
        }

        // A spawn ability sets up only the projectiles it spawns, so Fader's fissures and walls of fire, which it spawns
        // as area effects, take the factor of the creature that cast it here.
        private static IEnumerable<CodeInstruction> RememberSpawned(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var ability = AccessTools.Field(__originalMethod.DeclaringType, "<>4__this");
            var code = instructions.ToList();
            var spawnAt = Enumerable.Range(0, code.Count).Where(i => code[i].operand is MethodInfo method &&
                method.Name == nameof(UnityEngine.Object.Instantiate) && method.ReturnType == typeof(GameObject)).ToList();
            if (ability == null || spawnAt.Count != 1)
                throw new InvalidOperationException("The game's spawn ability changed.");
            code.InsertRange(spawnAt[0] + 1, new[]
            {
                new CodeInstruction(OpCodes.Dup),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, ability),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CreatureAreaDamage), nameof(Spawned))),
            });
            return code;
        }

        private static void Spawned(GameObject spawned, SpawnAbility ability)
        {
            var caster = SpawnerOf(ability);
            TamedBuildingDamage.Remember(spawned, caster);
            var area = spawned ? spawned.GetComponent<Aoe>() : null;
            if (area && caster && !caster.IsPlayer()) Remember(area, factorOf(caster));
        }
    }
}
