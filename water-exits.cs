using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A swimming creature holds its body about 2 m under the surface and stops swimming only where the bottom comes up
    // to meet it, so at a ledge whose top is just above the water, such as floating shore ice, it pushes against the edge
    // for good: wolves, frozen greydwarves and Fenrings stayed at the ice edge, while the ice top was 0.4 m above the water.
    // A Krigen's route ends in the water, so it floated where it fell in. Bjorn dove under the ice, and Deep North
    // skeletons, which cannot swim, sank to the seabed and walked about under it. Instead, a creature pressed against a
    // ledge for 2 s climbs onto the walkable top up to 1.8 m in front of it, at most 2.5 m above the water; one that stays put
    // for 2 s heads for such a ledge within 14 m, the one on the shortest way to its target; and one under a sheet for 2 s
    // comes up onto the sheet's top.
    // It climbs only onto natural ground and ice where its whole body fits, never onto buildings or ships. Players,
    // riders, fliers, bosses and creatures that live in the water are unchanged, and one idling in the water stays.
    internal static class WaterExits
    {
        private const float Patience = 2f, Progress = .5f, MaxLedge = 2.5f, Search = 14f, Interval = .25f, Heading = 6f;

        private sealed class State
        {
            internal Vector3 Anchor, Heading;
            internal float Since = -1f, UnderSince = -1f, NextProbe, HeadingUntil;
        }

        private static readonly ConditionalWeakTable<Character, State> States = new ConditionalWeakTable<Character, State>();
        private static readonly AccessTools.FieldRef<Character, Vector3> MoveDirection = AccessTools.FieldRefAccess<Character, Vector3>("m_moveDir");
        private static readonly AccessTools.FieldRef<Character, CapsuleCollider> Capsule = AccessTools.FieldRefAccess<Character, CapsuleCollider>("m_collider");
        // Natural ground and ice only: never onto a dock, a floor built over the water or a ship.
        private static readonly int Solid = LayerMask.GetMask("Default", "static_solid", "Default_small", "terrain");
        private static readonly int Blocking = LayerMask.GetMask("Default", "static_solid", "Default_small", "terrain", "piece", "vehicle", "character", "character_net");

        internal static void Initialize(IntegrationPatches harmony)
        {
            var method = AccessTools.Method(typeof(Character), "UpdateMotion") ?? throw new InvalidOperationException("Character's motion update moved.");
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(WaterExits), nameof(Steer)), postfix: new HarmonyMethod(typeof(WaterExits), nameof(Climb)));
        }

        private static bool Eligible(Character character)
        {
            if (character.IsPlayer() || !character.InWater()) return false;
            var ai = character.GetBaseAI();
            return ai && !(ai is MonsterAI monster && monster.m_avoidLand) && !character.IsBoss() && !character.IsFlying() && !character.HaveRider() && !character.IsAttached() && character.IsOwner();
        }

        // While heading for a ledge, swim towards it whatever the AI asks for.
        private static void Steer(Character __instance)
        {
            if (!States.TryGetValue(__instance, out var state) || state.HeadingUntil < Time.time || !Eligible(__instance)) return;
            MoveDirection(__instance) = state.Heading;
            __instance.SetLookDir(state.Heading);
        }

        private static void Climb(Character __instance)
        {
            if (!Eligible(__instance)) return;
            var state = States.GetOrCreateValue(__instance);
            float now = Time.time, surface = __instance.GetLiquidLevel();
            var position = __instance.transform.position;
            if (position.y > surface - .3f) { state.Since = state.UnderSince = -1f; state.HeadingUntil = 0f; return; }
            if (now < state.NextProbe) return;
            state.NextProbe = now + Interval;
            var capsule = Capsule(__instance);
            float radius = capsule ? capsule.radius * Mathf.Max(capsule.transform.lossyScale.x, capsule.transform.lossyScale.z) : .5f;

            // A sheet's underside doesn't stop a ray from below, so look for a climbable top over the creature from above.
            if (Top(position, surface, out var over) && over.y > position.y + .5f)
            {
                if (state.UnderSince < 0f) state.UnderSince = now;
                if (now - state.UnderSince >= Patience && TryTop(__instance, capsule, radius, position, surface)) Reset(state);
                return;
            }
            state.UnderSince = -1f;

            var move = MoveDirection(__instance); move.y = 0f;
            if (state.HeadingUntil >= now)
            {
                if (TryAhead(__instance, capsule, radius, position, state.Heading, surface)) Reset(state);
                return;
            }
            if (state.Since < 0f || Utils.DistanceXZ(position, state.Anchor) > Progress) { state.Anchor = position; state.Since = now; return; }
            if (now - state.Since < Patience) return;
            state.Since = now;
            if (move.sqrMagnitude > .01f && TryAhead(__instance, capsule, radius, position, move.normalized, surface)) { Reset(state); return; }
            // A ledge it could climb, searched in sixteen directions: the one on the shortest way to its target, or the nearest.
            // A creature idling in the water, such as one waiting in a swamp pool, stays.
            var target = __instance.GetBaseAI() is MonsterAI monster ? monster.GetTargetCreature() : null;
            if (!target && move.sqrMagnitude <= .01f) return;
            float best = float.MaxValue;
            for (int step = 0; step < 16; step++)
            {
                var direction = Quaternion.Euler(0f, step * 22.5f, 0f) * Vector3.forward;
                for (float distance = 2f; distance <= Search; distance += 2f)
                {
                    var ledge = position + direction * distance;
                    if (!Top(ledge, surface, out _)) continue;
                    float score = distance + (target ? Utils.DistanceXZ(ledge, target.transform.position) : 0f);
                    if (score < best) { best = score; state.Heading = direction; }
                    break;
                }
            }
            if (best < float.MaxValue) state.HeadingUntil = now + Heading;
        }

        private static void Reset(State state)
        {
            state.Since = state.UnderSince = -1f;
            state.HeadingUntil = 0f;
        }

        // A body can't press right up to a sheet whose underside reaches out under the water, so look up to 1.8 m past it.
        private static bool TryAhead(Character character, CapsuleCollider capsule, float radius, Vector3 position, Vector3 direction, float surface)
        {
            for (float reach = .6f; reach <= 1.8f; reach += .6f)
                if (TryTop(character, capsule, radius, position + direction * (radius + reach), surface)) return true;
            return false;
        }

        private static bool Top(Vector3 probe, float surface, out Vector3 point)
        {
            point = default;
            if (!Physics.Raycast(new Vector3(probe.x, surface + MaxLedge + .5f, probe.z), Vector3.down, out var top, MaxLedge + 1f, Solid, QueryTriggerInteraction.Ignore)) return false;
            if (top.normal.y < .7f || top.point.y < surface - .2f || top.point.y > surface + MaxLedge) return false;
            point = top.point;
            return true;
        }

        private static bool TryTop(Character character, CapsuleCollider capsule, float radius, Vector3 probe, float surface)
        {
            if (!Top(probe, surface, out var point)) return false;
            var feet = point + Vector3.up * .05f;
            var body = character.GetComponent<Rigidbody>();
            if (capsule)
            {
                float height = Mathf.Max(capsule.height * capsule.transform.lossyScale.y, radius * 2f);
                foreach (var blocker in Physics.OverlapCapsule(feet + Vector3.up * (radius + .05f), feet + Vector3.up * (height - radius), radius, Blocking, QueryTriggerInteraction.Ignore))
                    if (blocker.attachedRigidbody != body) return false;
            }
            character.transform.position = body.position = feet;
            body.linearVelocity = Vector3.zero;
            GameFields.OwnView(character)?.GetZDO()?.SetPosition(feet);
            return true;
        }
    }
}
