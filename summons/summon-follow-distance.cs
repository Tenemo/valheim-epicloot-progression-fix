using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The game stops a follower 3 m from its player's center, which a spirit moose's body nearly fills, so it slides into the
    // player, and stands under a player on a ledge. Staff summons following a player instead come to rest two of their own
    // body lengths away, measured along the ground between their body and the player's, wait until the player is 2 m farther
    // and run only when far behind. They still walk the game's own path, so doors, ice and water work as before.
    [HarmonyPatch(typeof(BaseAI), "Follow")]
    internal static class SummonFollowDistance
    {
        private const float BodyLengths = 2f, ResumeMargin = 2f, RunMargin = 7f;
        private static readonly ConditionalWeakTable<BaseAI, State> States = new ConditionalWeakTable<BaseAI, State>();

        private static bool followingAcrossFloors;

        [HarmonyPatch(typeof(BaseAI), "MoveTo")]
        private static class Arrival
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(Utils), nameof(Utils.DistanceXZ))),
                    AccessTools.Method(typeof(Arrival), nameof(Distance)), 2, "The navigation destination or waypoint arrival check changed.");

            private static float Distance(Vector3 first, Vector3 second) => followingAcrossFloors && Mathf.Abs(first.y - second.y) > 1f
                ? Vector3.Distance(first, second) : Utils.DistanceXZ(first, second);
        }

        private sealed class State
        {
            internal CapsuleCollider Capsule;
            internal bool Resting;
            internal float RunSpeed;
        }

        // The collision capsule along the creature's facing: its length when it lies along the body, its width when upright.
        internal static float BodyLength(CapsuleCollider capsule) =>
            (capsule.direction == 2 ? Mathf.Max(capsule.height, capsule.radius * 2f) : capsule.radius * 2f) * capsule.transform.lossyScale.z;

        // The ground distance between the creature's body and the player's.
        internal static float Gap(CapsuleCollider capsule, Player player) =>
            Utils.DistanceXZ(capsule.transform.TransformPoint(capsule.center), player.transform.position) - BodyLength(capsule) / 2f - player.GetRadius();

        internal static float TargetGap(CapsuleCollider capsule) => BodyLengths * BodyLength(capsule);

        // Walking slows by the creature's acceleration every physics step before it moves, so it slides this far after stopping.
        private static float Slide(Character creature, float speed) =>
            creature.m_acceleration > 0f ? Mathf.Max(0f, speed * (speed / creature.m_acceleration - 1f) * Time.fixedDeltaTime / 2f) : 0f;

        private static bool Prefix(BaseAI __instance, GameObject go, float dt)
        {
            var creature = GameFields.AiCharacter(__instance);
            var player = go ? go.GetComponent<Player>() : null;
            if (!player || !Companions.IsCompanion(creature)) return true;
            if (!States.TryGetValue(__instance, out var state)) States.Add(__instance, state = new State { Capsule = creature.GetComponent<CapsuleCollider>() });
            if (!state.Capsule) return true;
            float gap = Gap(state.Capsule, player), target = TargetGap(state.Capsule);
            var velocity = GameFields.CurrentVelocity(creature); velocity.y = 0f;
            float speed = velocity.magnitude;
            if (creature.IsRunning()) state.RunSpeed = speed;
            // The AI decides only once per update, so it stops at the update whose slide ends nearest the gap.
            float stop = Slide(creature, speed) + speed * dt / 2f;
            // A resting creature that is still sliding doesn't set off again.
            // A floor or ledge can be directly above the creature. Keep pathfinding until it reaches the player's elevation.
            // On a slope, or beside a player who rides or stands on something, the player is above but farther away than that.
            float rise = Mathf.Abs(player.transform.position.y - creature.transform.position.y);
            bool sameElevation = rise <= 1f || rise <= Utils.DistanceXZ(player.transform.position, creature.transform.position);
            if (sameElevation && gap <= target + (state.Resting ? Mathf.Max(ResumeMargin, stop) : stop))
            {
                state.Resting = true;
                __instance.StopMoving();
                return false;
            }
            state.Resting = false;
            // It drops from a run to its usual pace in time to stop from that pace instead of sliding in from a sprint.
            bool previous = followingAcrossFloors;
            followingAcrossFloors = !sameElevation;
            try { SummonedFollowers.MoveTo(__instance, dt, go.transform.position, 0f, gap > target + RunMargin + Slide(creature, state.RunSpeed)); }
            finally { followingAcrossFloors = previous; }
            return false;
        }
    }
}
