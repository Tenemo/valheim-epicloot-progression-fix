using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static partial class SleepEnemyPause
    {
        private static readonly int EnemyAttackKey = (Plugin.Id + ".enemy-attack").GetStableHashCode();
        private static readonly AccessTools.FieldRef<Projectile, Character> ProjectileOwner = AccessTools.FieldRefAccess<Projectile, Character>("m_owner");
        private static readonly AccessTools.FieldRef<Aoe, Character> AreaOwner = AccessTools.FieldRefAccess<Aoe, Character>("m_owner");
        private static readonly AccessTools.FieldRef<StatusEffect, Character> EffectOwner = AccessTools.FieldRefAccess<StatusEffect, Character>("m_character");
        private static readonly AccessTools.FieldRef<SE_Poison, float> PoisonLeft = AccessTools.FieldRefAccess<SE_Poison, float>("m_damageLeft");
        private static readonly ConditionalWeakTable<Component, AttackSource> AttackSources = new();
        private static readonly ConditionalWeakTable<StatusEffect, AttackSource> PeriodicSources = new();
        private static readonly Dictionary<GameObject, SuspendedObject> Suspended = new();
        // The attack or creature whose call is running on this thread, which decides whether what it emits is an enemy attack.
        // Most hooked calls emit nothing, so an attack's answer is worked out only when something asks for it.
        [ThreadStatic] private static Emission emission;

        private struct Emission
        {
            internal Component Attack;
            internal bool Enemy, Known;
        }

        private sealed class AttackSource { internal bool Enemy; }
        private sealed class SuspendedObject
        {
            internal Rigidbody Body;
            internal RigidbodyConstraints Constraints;
            internal Vector3 Velocity, AngularVelocity;
            internal Animator[] Animators;
            internal float[] Speeds;
            internal ParticleSystem[] Particles;
            internal Humanoid Creature;
            internal float Started;
        }

        // The native faction rules make untamed monsters hostile to players, except unaggravated dvergr.
        // AnimalAI wildlife has no attacks; allied creatures and player recovery keep running.
        private static bool Hostile(Character character)
        {
            if (!character || character.IsPlayer() || character.IsTamed() || character.GetFaction() == Character.Faction.Players) return false;
            var ai = character.GetComponent<MonsterAI>();
            if (!ai) return false;
            if (character.GetFaction() != Character.Faction.Dverger) return true;
            var view = GameFields.CharacterView(character);
            return ai.IsAggravatable() && view && view.IsValid() && view.GetZDO().GetBool(ZDOVars.s_aggravated);
        }

        // A creature still appearing is already held still and hidden, and growth and births tame theirs only after creating
        // them. Freezing one as an enemy would later free a body that has no collisions yet, and it would fall through the world.
        private static bool Halted(Character character) => Hostile(character) && !character.IsDead() && !FreshCreatureSpawns.Waiting(character);

        private static partial void InitializeSimulation(IntegrationPatches patches)
        {
            var actor = new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowCharacter)) { priority = Priority.First };
            foreach (var type in new[] { typeof(Character), typeof(Humanoid) })
                patches.Patch(AccessTools.DeclaredMethod(type, "CustomFixedUpdate"), prefix: actor);
            foreach (var type in new[] { typeof(BaseAI), typeof(MonsterAI) })
                patches.Patch(AccessTools.DeclaredMethod(type, "UpdateAI"), prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowAI)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(typeof(Character), "Awake"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(CharacterLoaded)));
            patches.Patch(AccessTools.Method(typeof(Humanoid), "Start"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(CharacterLoaded)));
            foreach (string method in new[] { "CustomFixedUpdate", "OnAnimatorMove", "Speed", "OnAttackTrigger", "Hit", "Jump", "Land", "TakeOff", "Stop", "TrailOn", "TrailOff", "Chain", "ResetChain" })
                patches.Patch(AccessTools.Method(typeof(CharacterAnimEvent), method), prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowAnimation)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(typeof(Humanoid), "OnAttackTrigger"), prefix: actor);
            foreach (string method in new[] { "OwnerSync", "ClientSync" })
                patches.Patch(AccessTools.Method(typeof(ZSyncTransform), method), prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowTransform)) { priority = Priority.First });

            foreach (var type in new[] { typeof(Projectile), typeof(Aoe) })
            {
                patches.Patch(AccessTools.Method(type, "Setup"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(RememberAttack)));
                patches.Patch(AccessTools.Method(type, "Awake"), postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(RememberEmittedAttack)));
            }
            foreach (var entry in new[] { (typeof(Projectile), "FixedUpdate"), (typeof(Projectile), "OnHit"), (typeof(Projectile), "LateUpdate"),
                (typeof(Aoe), "Update"), (typeof(Aoe), "CustomFixedUpdate"), (typeof(Aoe), "OnTriggerEnter"), (typeof(Aoe), "OnTriggerStay"), (typeof(Aoe), "OnHit") })
                patches.Patch(AccessTools.Method(entry.Item1, entry.Item2),
                    prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowAttack)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(SleepEnemyPause), nameof(EndAttack)));
            foreach (var type in new[] { typeof(Character), typeof(WearNTear), typeof(Destructible) })
                foreach (string method in new[] { "Damage", "RPC_Damage" })
                    patches.Patch(AccessTools.Method(type, method), prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowHit)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(typeof(Character), nameof(Character.ApplyDamage)),
                prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowHit)) { priority = Priority.First });

            patches.Patch(AccessTools.Method(typeof(SE_Poison), nameof(SE_Poison.AddDamage)),
                prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(BeforePoison)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AfterPoison)));
            foreach (string method in new[] { nameof(SE_Burning.AddFireDamage), nameof(SE_Burning.AddSpiritDamage) })
                patches.Patch(AccessTools.Method(typeof(SE_Burning), method),
                    prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowBurn)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AfterBurn)));
            foreach (var type in new[] { typeof(SE_Poison), typeof(SE_Burning) })
                patches.Patch(AccessTools.Method(type, "UpdateStatusEffect"), prefix: new HarmonyMethod(typeof(SleepEnemyPause), nameof(AllowPeriodic)) { priority = Priority.First });
        }

        private static void Freeze(GameObject instance)
        {
            if (!instance) return;
            var view = instance.GetComponent<ZNetView>();
            if (view && view.IsValid()) RememberEntityClocks(view.GetZDO());
            if (!Suspended.TryGetValue(instance, out var saved))
            {
                var body = instance.GetComponent<Rigidbody>();
                var creature = instance.GetComponent<Character>();
                var animators = instance.GetComponentsInChildren<Animator>(true);
                saved = new SuspendedObject { Body = body, Constraints = body ? body.constraints : default,
                    Velocity = creature ? creature.GetVelocity() : body ? body.linearVelocity : default, AngularVelocity = body ? body.angularVelocity : default,
                    Animators = animators, Speeds = animators.Select(animator => animator.speed).ToArray(),
                    Particles = instance.GetComponentsInChildren<ParticleSystem>(true).Where(particle => particle.isPlaying).ToArray(),
                    Creature = creature as Humanoid, Started = Time.time };
                Suspended.Add(instance, saved);
                PauseObjectSchedules(instance);
                foreach (var particle in saved.Particles) particle.Pause(false);
            }
            if (saved.Body)
            {
                saved.Body.constraints = RigidbodyConstraints.FreezeAll;
                if (!saved.Body.isKinematic) { saved.Body.linearVelocity = Vector3.zero; saved.Body.angularVelocity = Vector3.zero; }
            }
            foreach (var animator in saved.Animators) if (animator) animator.speed = 0f;
        }

        private static void Restore(GameObject instance, SuspendedObject saved)
        {
            if (!instance) return;
            if (saved.Body)
            {
                saved.Body.constraints = saved.Constraints;
                if (!saved.Body.isKinematic) { saved.Body.linearVelocity = saved.Velocity; saved.Body.angularVelocity = saved.AngularVelocity; }
            }
            for (int index = 0; index < saved.Animators.Length; index++) if (saved.Animators[index]) saved.Animators[index].speed = saved.Speeds[index];
            foreach (var particle in saved.Particles) if (particle) particle.Play(false);
            if (saved.Creature)
                foreach (var item in saved.Creature.GetInventory().GetAllItems()) item.m_lastAttackTime += Time.time - saved.Started;
        }

        private static bool AllowCharacter(Character __instance)
        {
            if (!Paused) return true;
            if (Halted(__instance)) { Freeze(__instance.gameObject); return false; }
            if (__instance && Suspended.TryGetValue(__instance.gameObject, out var saved))
            { Restore(__instance.gameObject, saved); Suspended.Remove(__instance.gameObject); }
            return true;
        }

        private static bool AllowAI(BaseAI __instance) => !Paused || AllowCharacter(__instance.GetComponent<Character>());
        private static bool AllowAnimation(CharacterAnimEvent __instance) => !Paused || AllowCharacter(__instance.GetComponentInParent<Character>());
        private static void CharacterLoaded(Character __instance) { if (Paused && Halted(__instance)) Freeze(__instance.gameObject); }

        private static void RememberAttack(Component __instance, Character owner)
        {
            bool enemy = owner ? Hostile(owner) : EnemyEmission();
            AttackSources.GetOrCreateValue(__instance).Enemy = enemy;
            var view = __instance.GetComponent<ZNetView>();
            if (view && view.IsValid() && view.IsOwner()) view.GetZDO().Set(EnemyAttackKey, enemy);
            if (Paused && enemy) Freeze(__instance.gameObject);
        }

        private static void RememberEmittedAttack(Component __instance)
        { if (EnemyEmission()) RememberAttack(__instance, null); }

        private static bool EnemyAttack(Component instance)
        {
            if (AttackSources.TryGetValue(instance, out var source)) return source.Enemy;
            var view = instance.GetComponent<ZNetView>();
            if (view && view.IsValid() && view.GetZDO().GetBool(EnemyAttackKey)) return true;
            return instance is Projectile projectile ? Hostile(ProjectileOwner(projectile)) : instance is Aoe area && Hostile(AreaOwner(area));
        }

        private static bool EnemyEmission()
        {
            if (!emission.Known)
            {
                emission.Enemy = emission.Attack is object && EnemyAttack(emission.Attack);
                emission.Known = true;
            }
            return emission.Enemy;
        }

        private static void BeginEmission(bool enemy, out Emission previous)
        {
            previous = emission;
            emission = new Emission { Enemy = enemy, Known = true };
        }

        private static bool AllowAttack(Component __instance, out Emission __state)
        {
            __state = emission;
            emission = new Emission { Attack = __instance };
            if (!Paused || !EnemyEmission()) return true;
            Freeze(__instance.gameObject);
            return false;
        }

        // A projectile's hit runs inside its own flight step; the step keeps the answer its hit worked out.
        private static void EndAttack(Emission __state)
        {
            if (!__state.Known && emission.Known && __state.Attack is object && ReferenceEquals(__state.Attack, emission.Attack))
                __state = emission;
            emission = __state;
        }

        private static bool AllowTransform(ZSyncTransform __instance)
        {
            if (!Paused) return true;
            var character = __instance.GetComponent<Character>();
            if (character) return AllowCharacter(character);
            Component attack = __instance.GetComponent<Projectile>();
            if (!attack) attack = __instance.GetComponent<Aoe>();
            if (!attack || !EnemyAttack(attack)) return true;
            Freeze(__instance.gameObject);
            return false;
        }

        private static bool EnemyHit(HitData hit)
        {
            if (hit == null) return false;
            var attacker = hit.GetAttacker();
            return attacker ? Hostile(attacker) : hit.m_hitType == HitData.HitType.EnemyHit || EnemyEmission();
        }

        private static bool AllowHit(HitData hit) => !Paused || !EnemyHit(hit);

        private static void RememberPeriodic(StatusEffect effect)
        {
            var hit = CorpseRotKillCredit.HitReceivedBy(EffectOwner(effect));
            if (hit == null || !hit.GetAttacker()) return;
            PeriodicSources.GetOrCreateValue(effect).Enemy = EnemyHit(hit);
        }

        private static bool BeforePoison(SE_Poison __instance, float damage, out bool __state)
        {
            bool allowed = !Paused || !EnemyHit(CorpseRotKillCredit.HitReceivedBy(EffectOwner(__instance)));
            __state = allowed && damage >= PoisonLeft(__instance);
            return allowed;
        }
        private static void AfterPoison(SE_Poison __instance, bool __state) { if (__state) RememberPeriodic(__instance); }
        private static bool AllowBurn(SE_Burning __instance) => !Paused || !EnemyHit(CorpseRotKillCredit.HitReceivedBy(EffectOwner(__instance)));
        private static void AfterBurn(SE_Burning __instance, bool __result) { if (__result) RememberPeriodic(__instance); }
        private static bool AllowPeriodic(StatusEffect __instance) => !Paused || !PeriodicSources.TryGetValue(__instance, out var source) || !source.Enemy;

        private static partial void SuspendSimulation()
        {
            foreach (var character in Character.GetAllCharacters()) if (Halted(character)) Freeze(character.gameObject);
            foreach (var projectile in UnityEngine.Object.FindObjectsByType<Projectile>(FindObjectsSortMode.None)) if (EnemyAttack(projectile)) Freeze(projectile.gameObject);
            foreach (var area in UnityEngine.Object.FindObjectsByType<Aoe>(FindObjectsSortMode.None)) if (EnemyAttack(area)) Freeze(area.gameObject);
        }

        private static partial void ResumeSimulation()
        {
            foreach (var entry in Suspended) Restore(entry.Key, entry.Value);
            Suspended.Clear();
            emission = default;
        }
    }
}
