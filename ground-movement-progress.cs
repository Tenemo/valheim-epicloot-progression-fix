using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class GroundMovementProgress
    {
        private const float ObservationSeconds = 2f, StalledSeconds = 8f;
        private static readonly ConditionalWeakTable<BaseAI, State> States = new();
        private static readonly AccessTools.FieldRef<BaseAI, List<Vector3>> Path = AccessTools.FieldRefAccess<BaseAI, List<Vector3>>("m_path");
        private static readonly AccessTools.FieldRef<BaseAI, float> PathTime = AccessTools.FieldRefAccess<BaseAI, float>("m_lastFindPathTime");
        private static readonly AccessTools.FieldRef<Character, Vector3> MoveDirection = AccessTools.FieldRefAccess<Character, Vector3>("m_moveDir");
        private static readonly AccessTools.FieldRef<Character, Vector3> DesiredVelocity = AccessTools.FieldRefAccess<Character, Vector3>("m_currentVel");
        [ThreadStatic] private static int fleeing;

        private sealed class State
        {
            internal Character Character, Target;
            internal Pathfinding Navigation;
            internal Pathfinding.AgentType Profile;
            internal long Owner;
            internal ushort OwnerRevision;
            internal Vector3 Goal, TargetPosition, Position, Waypoint, BlockerPosition;
            internal Quaternion BlockerRotation;
            internal Collider Blocker;
            internal float Attempted, ObservedAt, NoProgress, LastAttempt;
            internal double Step = double.MinValue;
            internal int Repaths;
            internal bool Unreachable, Flying, Swimming;
        }

        [HarmonyPatch(typeof(BaseAI), "Flee"), HarmonyPrefix]
        private static void BeginFlee(out bool __state) { __state = true; fleeing++; }
        [HarmonyPatch(typeof(BaseAI), "Flee"), HarmonyFinalizer]
        private static Exception EndFlee(Exception __exception, bool __state) { if (__state) fleeing--; return __exception; }

        private static void Invalidate(BaseAI ai) { Path(ai).Clear(); PathTime(ai) = float.NegativeInfinity; }

        private static bool ObstacleChanged(State state) => !ReferenceEquals(state.Blocker, null) &&
            (!state.Blocker || !state.Blocker.enabled || !state.Blocker.gameObject.activeInHierarchy ||
                Vector3.Distance(state.BlockerPosition, state.Blocker.transform.position) > .1f ||
                Quaternion.Angle(state.BlockerRotation, state.Blocker.transform.rotation) > 3f);

        private static void ClearFailure(State state)
        {
            state.NoProgress = 0f; state.Repaths = 0; state.Unreachable = false; state.Blocker = null;
        }

        internal static bool Stalled(MonsterAI ai, Character target)
        {
            if (!target || !States.TryGetValue(ai, out var state) || !state.Character) return false;
            bool flying = state.Character.IsFlying(), swimming = state.Character.IsSwimming();
            if (state.Flying != flying || state.Swimming != swimming)
            {
                States.Remove(ai);
                if (state.Character.IsOwner()) Invalidate(ai);
                return false;
            }
            if (!state.Unreachable || state.Target != target || flying || swimming || Time.time - state.LastAttempt > 20f ||
                Vector3.Distance(target.transform.position, state.TargetPosition) > 1f || state.Navigation != Pathfinding.instance || state.Profile != ai.m_pathAgentType) return false;
            var data = GameFields.CharacterView(state.Character).GetZDO();
            if (data == null || state.Owner != data.GetOwner() || state.OwnerRevision != data.OwnerRevision) return false;
            if (ObstacleChanged(state)) { ClearFailure(state); Invalidate(ai); return false; }
            return true;
        }

        [HarmonyPatch(typeof(BaseAI), "MoveTo"), HarmonyPostfix]
        private static void Observe(BaseAI __instance, float dt, Vector3 point, bool __result)
        {
            if (fleeing > 0) return;
            var character = __instance.GetComponent<Character>();
            if (!character || character.IsPlayer() || !character.IsOwner()) return;
            var data = GameFields.CharacterView(character).GetZDO();
            var target = (__instance as MonsterAI)?.GetTargetCreature();
            var state = States.GetValue(__instance, _ => new State { Character = character });
            bool flying = character.IsFlying(), swimming = character.IsSwimming();
            bool reset = state.Target != target || state.Owner != data.GetOwner() || state.OwnerRevision != data.OwnerRevision ||
                state.Profile != __instance.m_pathAgentType || state.Navigation != Pathfinding.instance || Vector3.Distance(state.Goal, point) > 1f ||
                state.Flying != flying || state.Swimming != swimming || Time.time - state.LastAttempt > ObservationSeconds * 2f;
            if (reset)
            {
                state.Target = target; state.TargetPosition = target ? target.transform.position : point;
                state.Owner = data.GetOwner(); state.OwnerRevision = data.OwnerRevision; state.Profile = __instance.m_pathAgentType;
                state.Navigation = Pathfinding.instance; state.Goal = point; state.Position = character.transform.position;
                state.Flying = flying; state.Swimming = swimming;
                state.Attempted = state.ObservedAt = 0f; ClearFailure(state);
            }
            var path = Path(__instance);
            var attack = character is Humanoid humanoid ? GameFields.CurrentAttack(humanoid) : null;
            if (__result || path.Count == 0 || character.IsFlying() || character.IsSwimming() || !character.IsOnGround() ||
                character.IsDead() || character.HaveRider() || character.InAttack() || attack != null && !attack.IsDone() ||
                character.IsStaggering() || __instance.IsSleeping() || (__instance is MonsterAI monster && monster.IsCharging()) ||
                TameRiding.HoldingPosition(character) || FreshCreatureSpawns.Waiting(character) || MoveDirection(character).sqrMagnitude < .001f ||
                CreatureRouteReadiness.Unbuilt(character.transform.position, __instance.m_pathAgentType) || CreatureRouteReadiness.Unbuilt(path[0], __instance.m_pathAgentType))
            {
                state.Position = character.transform.position; state.ObservedAt = state.Attempted;
                if (path.Count > 0) state.Waypoint = path[0];
                return;
            }
            state.LastAttempt = Time.time;
            if (state.Step == Time.fixedTimeAsDouble) return;
            state.Step = Time.fixedTimeAsDouble;
            if (ObstacleChanged(state)) { ClearFailure(state); Invalidate(__instance); return; }
            state.Attempted += dt;
            var waypoint = path[0];
            if (Vector3.Distance(waypoint, state.Waypoint) > .25f)
            {
                state.NoProgress = 0f; state.Unreachable = false;
                state.Waypoint = waypoint; state.Position = character.transform.position; state.ObservedAt = state.Attempted;
                return;
            }
            float elapsed = state.Attempted - state.ObservedAt;
            if (elapsed < ObservationSeconds) return;
            var direction = Vector3.ProjectOnPlane(waypoint - state.Position, Vector3.up).normalized;
            float progress = Vector3.Dot(character.transform.position - state.Position, direction);
            float expected = Vector3.ProjectOnPlane(DesiredVelocity(character), Vector3.up).magnitude * elapsed;
            if (progress > Mathf.Max(.002f, expected * .02f)) ClearFailure(state);
            else state.NoProgress += elapsed;
            state.Position = character.transform.position; state.ObservedAt = state.Attempted;
            if (state.NoProgress < StalledSeconds) return;
            if (state.Repaths == 0)
            {
                state.Repaths = 1; state.NoProgress = 0f; Invalidate(__instance);
                return;
            }
            state.Unreachable = true;
            if (!state.Blocker)
            {
                var body = character.GetComponent<Rigidbody>();
                float nearest = float.PositiveInfinity;
                foreach (var hit in body.SweepTestAll(direction, .5f, QueryTriggerInteraction.Ignore))
                    if (hit.collider && hit.normal.y < .75f && Vector3.Dot(hit.normal, direction) < -.2f && hit.distance < nearest)
                    { state.Blocker = hit.collider; nearest = hit.distance; }
                if (state.Blocker)
                {
                    state.BlockerPosition = state.Blocker.transform.position;
                    state.BlockerRotation = state.Blocker.transform.rotation;
                }
            }
        }
    }
}
