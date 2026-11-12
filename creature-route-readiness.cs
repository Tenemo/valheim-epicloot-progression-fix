using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace EpicLootProgressionFix
{
    // Native MoveTo treats an unfinished route as arrival. Keep the requested destination while loaded navigation
    // tiles are being built, retry at the native one-second limit, and prepare the loaded corridor for long requests.
    [HarmonyPatch]
    internal static class CreatureRouteReadiness
    {
        private static readonly Func<Pathfinding, Vector3, Pathfinding.AgentType, Vector3Int> TileId =
            AccessTools.MethodDelegate<Func<Pathfinding, Vector3, Pathfinding.AgentType, Vector3Int>>(
                AccessTools.Method(typeof(Pathfinding), "GetTile", new[] { typeof(Vector3), typeof(Pathfinding.AgentType) }));
        // Moving creatures ask this twice per physics step. The tile type is private, so its typed lookup is made once.
        private static readonly Func<Pathfinding, Vector3Int, bool> TileBuilt = MakeTileBuilt(AccessTools.Inner(typeof(Pathfinding), "NavMeshTile"));
        private static readonly Action<Pathfinding, Vector3, Pathfinding.AgentType> Poke =
            AccessTools.MethodDelegate<Action<Pathfinding, Vector3, Pathfinding.AgentType>>(AccessTools.Method(typeof(Pathfinding), "PokeArea"));
        // Creatures whose last route request failed on navigation still being built.
        private static readonly ConditionalWeakTable<BaseAI, object> Waiting = new ConditionalWeakTable<BaseAI, object>();
        private static readonly object Marker = new object();
        private static bool keepDestination;

        private static bool Eligible(BaseAI ai)
        {
            var character = ai.GetComponent<Character>(); var view = ai.GetComponent<ZNetView>();
            // Retain the existing bounty behavior, including its non-ground movement profiles.
            if (view && view.IsValid() && view.GetZDO().GetString(BountyCreature.IdKey).Length > 0) return true;
            var attack = character is Humanoid humanoid ? GameFields.CurrentAttack(humanoid) : null;
            return character && !character.IsPlayer() && character.IsOwner() && !character.IsDead() && !character.IsFlying() && !character.IsSwimming() &&
                !character.HaveRider() && !character.InAttack() && (attack == null || attack.IsDone()) && !character.IsStaggering() &&
                !(ai is MonsterAI monster && monster.IsCharging()) && !ai.IsSleeping() && !TameRiding.HoldingPosition(character) && !FreshCreatureSpawns.Waiting(character);
        }

        private static class Tiles<T> where T : class
        {
            internal static readonly AccessTools.FieldRef<Pathfinding, Dictionary<Vector3Int, T>> All =
                AccessTools.FieldRefAccess<Pathfinding, Dictionary<Vector3Int, T>>("m_tiles");
            internal static readonly AccessTools.FieldRef<T, NavMeshDataInstance> Mesh = AccessTools.FieldRefAccess<T, NavMeshDataInstance>("m_instance");
        }

        private static Func<Pathfinding, Vector3Int, bool> MakeTileBuilt(Type tile)
        {
            // Resolve the tile fields now, so a changed field fails at startup instead of on the first moving creature.
            RuntimeHelpers.RunClassConstructor(typeof(Tiles<>).MakeGenericType(tile).TypeHandle);
            return (Func<Pathfinding, Vector3Int, bool>)Delegate.CreateDelegate(typeof(Func<Pathfinding, Vector3Int, bool>),
                AccessTools.Method(typeof(CreatureRouteReadiness), nameof(Built)).MakeGenericMethod(tile));
        }

        private static bool Built<T>(Pathfinding navigation, Vector3Int id) where T : class =>
            Tiles<T>.All(navigation).TryGetValue(id, out var tile) && tile != null && Tiles<T>.Mesh(tile).valid;

        // A tile is only created when asked for and gets its mesh once its first build finishes.
        internal static bool Unbuilt(Vector3 point, Pathfinding.AgentType agent) => !TileBuilt(Pathfinding.instance, TileId(Pathfinding.instance, point, agent));

        [HarmonyPrefix, HarmonyPatch(typeof(BaseAI), "FindPath")]
        private static void AskAgain(BaseAI __instance, Vector3 target, bool ___m_lastFindPathResult, Vector3 ___m_lastFindPathTarget, ref float ___m_lastFindPathTime)
        {
            bool eligible = Eligible(__instance);
            if (!eligible) Waiting.Remove(__instance);
            // Moving the failed request back past the five-second reuse makes the native method ask again.
            if (eligible && !___m_lastFindPathResult && Time.time - ___m_lastFindPathTime >= 1f && Waiting.TryGetValue(__instance, out _))
                ___m_lastFindPathTime -= 5f;
            var character = __instance.GetComponent<Character>();
            if (!eligible && !(character && character.IsOwner() && SummonOwnership.IsFollower(character))) return;
            float age = Time.time - ___m_lastFindPathTime;
            if (age < 1f || age < 5f && Vector3.Distance(target, ___m_lastFindPathTarget) < 1f || !Pathfinding.instance || !ZoneSystem.instance) return;
            int steps = Mathf.CeilToInt(Utils.DistanceXZ(__instance.transform.position, target) / Pathfinding.instance.m_tileSize);
            for (int step = 1; step < steps; step++)
            {
                var point = Vector3.Lerp(__instance.transform.position, target, step / (float)steps);
                if (ZoneSystem.instance.IsZoneLoaded(ZoneSystem.GetZone(point))) Poke(Pathfinding.instance, point, __instance.m_pathAgentType);
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(BaseAI), "FindPath")]
        private static void NoteResult(BaseAI __instance, Vector3 target, float ___m_lastFindPathTime, bool __result)
        {
            // A request sets its time to now; a reused result keeps the note of the request it came from.
            if (___m_lastFindPathTime == Time.time)
            {
                Waiting.Remove(__instance);
                if (!__result && Eligible(__instance) &&
                    (Unbuilt(__instance.transform.position, __instance.m_pathAgentType) || Unbuilt(target, __instance.m_pathAgentType)))
                    Waiting.Add(__instance, Marker);
            }
            keepDestination = !__result && Waiting.TryGetValue(__instance, out _);
        }

        // MoveTo is FindPath's only caller and returns as soon as the route fails.
        [HarmonyPrefix, HarmonyPatch(typeof(BaseAI), "MoveTo")]
        private static void BeforeMove() => keepDestination = false;

        [HarmonyPostfix, HarmonyPatch(typeof(BaseAI), "MoveTo")]
        private static void AfterMove(BaseAI __instance, Vector3 point, float dist, bool run, ref bool __result)
        {
            if (__result && (WalkStraight(__instance, point, dist, run) || keepDestination)) __result = false;
            keepDestination = false;
        }

        // Fitted navigation can cut a large follower off from ground it can physically reach, and its tiles can take a while
        // to build. A follower whose route gives out short of its goal heads straight for it, unless water or lava lies ahead.
        private static bool WalkStraight(BaseAI ai, Vector3 point, float dist, bool run)
        {
            var character = ai.GetComponent<Character>();
            var position = ai.transform.position;
            if (Utils.DistanceXZ(point, position) < Mathf.Max(dist, 1f) || !SummonOwnership.IsFollower(character) || !Eligible(ai)) return false;
            var direction = point - position; direction.y = 0f; direction.Normalize();
            var ahead = position + direction * (character.GetRadius() + 2f);
            if (!ZoneSystem.instance.GetSolidHeight(ahead, out float height) || height < ZoneSystem.instance.m_waterLevel || ZoneSystem.IsLavaPreHeightmap(ahead, .1f)) return false;
            ai.MoveTowards(direction, run);
            return true;
        }
    }
}
