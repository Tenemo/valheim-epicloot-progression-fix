using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class CreatureReachability
    {
        private const float CheckInterval = 2f, StalledSeconds = 8f;
        private static readonly ConditionalWeakTable<MonsterAI, State> States = new ConditionalWeakTable<MonsterAI, State>();
        private static readonly AccessTools.FieldRef<MonsterAI, Character> Target = AccessTools.FieldRefAccess<MonsterAI, Character>("m_targetCreature");
        private static readonly AccessTools.FieldRef<MonsterAI, float> SinceAttacking = AccessTools.FieldRefAccess<MonsterAI, float>("m_timeSinceAttacking");
        private static readonly AccessTools.FieldRef<BaseAI, float> SinceHurt = AccessTools.FieldRefAccess<BaseAI, float>("m_timeSinceHurt");
        private static readonly Func<BaseAI, Vector3, float, float, bool> CanMove = AccessTools.MethodDelegate<Func<BaseAI, Vector3, float, float, bool>>(
            AccessTools.Method(typeof(BaseAI), "CanMove"));
        private static readonly Func<BaseAI, bool> TakingOff = AccessTools.MethodDelegate<Func<BaseAI, bool>>(AccessTools.Method(typeof(BaseAI), "IsTakingOff"));

        private sealed class State
        {
            internal Humanoid Character;
            internal Character Target;
            internal bool Flying, Swimming, Unreachable, WaitingNavigation;
            internal long Owner;
            internal ushort OwnerRevision;
            internal Vector3 TargetPosition;
            internal Bounds Explored;
            internal float BestDistance, LastProgress, NextObservation, NextCheck;
            internal float LastPursuit = float.NegativeInfinity;
            internal int FailedPaths;
            internal readonly List<Vector3> Path = new List<Vector3>();
        }

        private static State Current(MonsterAI ai, Character target)
        {
            var state = States.GetValue(ai, value => new State { Character = value.GetComponent<Humanoid>() });
            bool flying = state.Character.IsFlying();
            bool swimming = state.Character.IsSwimming();
            var data = GameFields.CharacterView(state.Character).GetZDO();
            long owner = data.GetOwner();
            if (state.Target != target || state.Flying != flying || state.Swimming != swimming || state.Owner != owner || state.OwnerRevision != data.OwnerRevision ||
                Vector3.Distance(state.TargetPosition, target.transform.position) > 2f)
            {
                state.Target = target; state.Flying = flying; state.Swimming = swimming; state.Owner = owner; state.OwnerRevision = data.OwnerRevision; state.TargetPosition = target.transform.position;
                state.Explored = new Bounds(ai.transform.position, Vector3.one * 2f);
                state.BestDistance = Vector3.Distance(ai.transform.position, target.transform.position);
                state.LastProgress = Time.time; state.LastPursuit = float.NegativeInfinity;
                state.NextObservation = 0f; state.FailedPaths = 0; state.Unreachable = false;
            }
            return state;
        }

        internal static void Initialize(IntegrationPatches harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MonsterAI), nameof(MonsterAI.UpdateAI)),
                transpiler: new HarmonyMethod(typeof(CreatureReachability), nameof(RetreatCondition)));
        }

        private static IEnumerable<CodeInstruction> RetreatCondition(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var flag = AccessTools.Field(typeof(MonsterAI), nameof(MonsterAI.m_fleeIfHurtWhenTargetCantBeReached));
            var reads = code.Where(instruction => instruction.LoadsField(flag)).ToArray();
            if (reads.Length != 1) throw new InvalidOperationException("The hurt/unreachable retreat condition changed.");
            reads[0].opcode = OpCodes.Call;
            reads[0].operand = AccessTools.Method(typeof(CreatureReachability), nameof(FleeWhenHurt));
            return code;
        }

        // The native branch still checks its original timers. Do expensive work only when all of them hold.
        internal static bool FleeWhenHurt(MonsterAI ai)
        {
            if (!ai.m_fleeIfHurtWhenTargetCantBeReached) return false;
            var target = Target(ai);
            if (!target || SinceAttacking(ai) <= 30f || SinceHurt(ai) >= 20f) return true;
            return Unreachable(ai, target);
        }

        private static bool OffensiveAttack(MonsterAI ai, ItemDrop.ItemData item) =>
            item.m_shared.m_aiTargetType == ItemDrop.ItemData.AiTarget.Enemy &&
            item.m_shared.m_attack != null && !string.IsNullOrEmpty(item.m_shared.m_attack.m_attackAnimation) &&
            ai.CanUseAttack(item);

        private static float EffectiveRange(MonsterAI ai, ItemDrop.ItemData item) =>
            CreatureMeleeReach.AttackRange(LateBiomePressure.AttackRange(item, ai), ai, item);

        private static float AttackRange(MonsterAI ai, Humanoid character)
        {
            float range = 0f;
            foreach (var item in character.GetInventory().GetAllItems())
                if (OffensiveAttack(ai, item))
                    range = Mathf.Max(range, EffectiveRange(ai, item));
            return range;
        }

        private static bool CanAttackFrom(MonsterAI ai, Humanoid character, Character target, Vector3 position)
        {
            float distance = Vector3.Distance(position, target.transform.position) - target.GetRadius();
            bool weapon = false;
            foreach (var item in character.GetInventory().GetAllItems())
                if (OffensiveAttack(ai, item) &&
                    distance >= item.m_shared.m_aiAttackRangeMin && distance < EffectiveRange(ai, item)) { weapon = true; break; }
            if (!weapon) return false;
            return BaseAI.CanSeeTarget(character.transform, position + character.GetEyePoint() - character.transform.position,
                float.MaxValue, 360f, true, ai.m_mistVision, target);
        }

        private static bool GroundAttackPosition(MonsterAI ai, Humanoid character, Character target, State state)
        {
            state.WaitingNavigation = Pathfinding.instance == null;
            if (state.WaitingNavigation) return false;
            bool Reaches(Vector3 candidate)
            {
                bool reached = CreatureNavigation.GetFullPath(ai, character.transform.position, candidate, state.Path, false);
                if (CreatureRouteReadiness.Unbuilt(character.transform.position, ai.m_pathAgentType) || CreatureRouteReadiness.Unbuilt(candidate, ai.m_pathAgentType))
                    state.WaitingNavigation = true;
                return reached && state.Path.Count > 0 && CanAttackFrom(ai, character, target, state.Path[state.Path.Count - 1]);
            }
            if (Reaches(target.transform.position)) return true;
            float range = AttackRange(ai, character);
            foreach (float fraction in new[] { .75f, .35f })
                for (int direction = 0; direction < 8; direction++)
                    if (Reaches(target.transform.position + Quaternion.Euler(0f, direction * 45f, 0f) * Vector3.forward * range * fraction)) return true;
            return false;
        }

        internal static bool Unreachable(MonsterAI ai, Character target)
        {
            var state = Current(ai, target);
            var character = state.Character;
            if (character.InAttack() || ai.IsCharging() || TakingOff(ai))
            {
                state.LastProgress = Time.time; state.Unreachable = false; state.FailedPaths = 0;
                return false;
            }
            if (Time.time < state.NextCheck) return state.Unreachable;
            state.NextCheck = Time.time + CheckInterval;
            if (CanAttackFrom(ai, character, target, character.transform.position))
            {
                state.LastProgress = Time.time; state.FailedPaths = 0;
                return state.Unreachable = false;
            }
            if (!state.Flying)
            {
                if (!state.Swimming && GroundMovementProgress.Stalled(ai, target)) return state.Unreachable = true;
                bool reachable = GroundAttackPosition(ai, character, target, state);
                state.FailedPaths = reachable || state.WaitingNavigation ? 0 : state.FailedPaths + 1;
                return state.Unreachable = state.FailedPaths >= 2;
            }
            var delta = target.transform.position - character.transform.position;
            bool blocked = delta.sqrMagnitude > .01f && !CanMove(ai, delta.normalized, character.GetRadius(), delta.magnitude);
            if (!blocked) state.LastProgress = Time.time;
            return state.Unreachable = blocked && Time.time - state.LastPursuit <= CheckInterval * 2f && Time.time - state.LastProgress >= StalledSeconds;
        }

        [HarmonyPatch(typeof(BaseAI), "MoveTo")]
        private static class Pursuit
        {
            private static void Postfix(BaseAI __instance, Vector3 point)
            {
                if (!(__instance is MonsterAI ai) || !ai.m_fleeIfHurtWhenTargetCantBeReached) return;
                var character = ai.GetComponent<Humanoid>();
                if (!character || !character.IsOwner()) return;
                if (!character.IsFlying()) return;
                var target = Target(ai);
                if (!target || Vector3.Distance(point, target.transform.position) > 2f) return;
                var state = Current(ai, target);
                if (character.InAttack() || ai.IsCharging() || TakingOff(ai)) { state.LastProgress = Time.time; return; }
                if (Time.time - state.LastPursuit > CheckInterval * 2f)
                {
                    state.Explored = new Bounds(character.transform.position, Vector3.one * 2f);
                    state.BestDistance = Vector3.Distance(character.transform.position, target.transform.position);
                    state.LastProgress = Time.time; state.Unreachable = false;
                }
                state.LastPursuit = Time.time;
                if (Time.time < state.NextObservation) return;
                state.NextObservation = Time.time + CheckInterval;
                float distance = Vector3.Distance(character.transform.position, target.transform.position);
                if (!state.Explored.Contains(character.transform.position) || distance < state.BestDistance - 1f)
                {
                    state.Explored.Encapsulate(character.transform.position); state.Explored.Expand(2f);
                    state.BestDistance = Mathf.Min(state.BestDistance, distance);
                    state.LastProgress = Time.time; state.Unreachable = false;
                }
            }
        }
    }
}
