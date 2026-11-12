using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class CreatureNavigation
    {
        private static readonly FieldInfo Agents = AccessTools.Field(typeof(Pathfinding), "m_agentSettings");
        private static readonly Type Settings = AccessTools.Inner(typeof(Pathfinding), "AgentSettings");
        private static readonly FieldInfo Build = AccessTools.Field(Settings, "m_build");
        private static readonly FieldInfo[] Rules = new[] { "m_canWalk", "m_avoidWater", "m_canSwim", "m_swimDepth", "m_areaMask" }
            .Select(name => AccessTools.Field(Settings, name)).ToArray();
        // The refresh reads a profile's settings four times a second for every creature this game simulates.
        private static readonly AccessTools.FieldRef<object, NavMeshBuildSettings> BuildOf = AccessTools.FieldRefAccess<object, NavMeshBuildSettings>(Build);
        private static readonly AccessTools.FieldRef<object, bool> CanWalk = AccessTools.FieldRefAccess<object, bool>(Rules[0]),
            AvoidWater = AccessTools.FieldRefAccess<object, bool>(Rules[1]), CanSwim = AccessTools.FieldRefAccess<object, bool>(Rules[2]);
        private static readonly AccessTools.FieldRef<object, float> SwimDepth = AccessTools.FieldRefAccess<object, float>(Rules[3]);
        private static readonly MethodInfo AddAgent = AccessTools.Method(typeof(Pathfinding), "AddAgent");
        private static readonly AccessTools.FieldRef<BaseAI, float> LastPathTime = AccessTools.FieldRefAccess<BaseAI, float>("m_lastFindPathTime");
        private static readonly AccessTools.FieldRef<BaseAI, bool> LastPathResult = AccessTools.FieldRefAccess<BaseAI, bool>("m_lastFindPathResult");
        private static readonly AccessTools.FieldRef<BaseAI, List<Vector3>> Path = AccessTools.FieldRefAccess<BaseAI, List<Vector3>>("m_path");
        private static readonly Dictionary<(Pathfinding.AgentType Source, int Radius, int Height, int SwimDepth), Pathfinding.AgentType> Profiles = new();
        private static readonly Dictionary<Pathfinding.AgentType, Pathfinding.AgentType> Originals = new();
        private static readonly ConditionalWeakTable<BaseAI, State> States = new();
        private static Pathfinding navigation;
        private static IList profiles;
        private static IEnumerable<MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(BaseAI), "FindPath"), AccessTools.Method(typeof(BaseAI), "HavePath")
        };

        static CreatureNavigation()
        {
            if (Agents == null || Build == null || AddAgent == null || Rules.Any(rule => rule == null))
                throw new InvalidOperationException("The native navigation profile fields changed.");
        }

        private sealed class State
        {
            internal Pathfinding Navigation;
            internal Character Character;
            internal Pathfinding.AgentType Original, Fitted;
            internal Vector3 Scale;
            internal long Owner;
            internal ushort OwnerRevision;
            internal float RefreshAt, Reach;
            internal bool Ground;
        }

        private static readonly List<Vector3> NativeRoute = new List<Vector3>();

        [HarmonyPriority(Priority.First)]
        private static void Prefix(BaseAI __instance) => Fit(__instance);

        // A fitted profile can leave a large creature, such as a giant Seeker Soldier among Mistlands rocks, on a scrap of
        // navigation, where its route stops at the scrap's edge or doesn't exist, and the native MoveTo stops a creature at
        // its route's end, so it stood and took hits from an enemy it could walk to. Where the built fitted route doesn't
        // bring the body to the destination and the native profile's route gets closer, a wild creature or tame takes that,
        // as it would without the fit. Summoned followers keep their own straight walk.
        [HarmonyPriority(Priority.First)]
        private static void Postfix(BaseAI __instance, Vector3 target, MethodBase __originalMethod, ref bool __result)
        {
            bool find = __originalMethod.Name == "FindPath";
            // A reused route belongs to an earlier request; a native HavePath answers only whether a complete route exists.
            if (find ? LastPathTime(__instance) != Time.time : __result) return;
            if (!States.TryGetValue(__instance, out var state) || !state.Ground || state.Fitted == state.Original || __instance.m_pathAgentType != state.Fitted ||
                state.Navigation != Pathfinding.instance) return;
            var path = Path(__instance);
            float fitted = !find || !__result || path.Count == 0 ? float.PositiveInfinity : Utils.DistanceXZ(path[path.Count - 1], target);
            if (fitted <= state.Reach + 1f || !Fallback(__instance, state, target)) return;
            var position = __instance.transform.position;
            if (!find) { __result = Pathfinding.instance.HavePath(position, target, state.Original); return; }
            if (!Pathfinding.instance.GetPath(position, target, NativeRoute, state.Original) || NativeRoute.Count == 0 ||
                Utils.DistanceXZ(NativeRoute[NativeRoute.Count - 1], target) + 1f >= fitted) return;
            path.Clear(); path.AddRange(NativeRoute);
            LastPathResult(__instance) = __result = true;
        }

        // Followers keep their straight walk, and while the fitted tiles at either end are still building, route
        // readiness waits for them.
        private static bool Fallback(BaseAI ai, State state, Vector3 target) => !SummonOwnership.IsFollower(state.Character) &&
            !CreatureRouteReadiness.Unbuilt(ai.transform.position, state.Fitted) && !CreatureRouteReadiness.Unbuilt(target, state.Fitted);

        // A complete route with the same fallback, for checks that look ahead of the creature's movement.
        internal static bool GetFullPath(BaseAI ai, Vector3 from, Vector3 to, List<Vector3> path, bool cleanup)
        {
            if (Pathfinding.instance.GetPath(from, to, path, ai.m_pathAgentType, true, cleanup)) return true;
            return States.TryGetValue(ai, out var state) && state.Ground && state.Fitted != state.Original && ai.m_pathAgentType == state.Fitted &&
                state.Navigation == Pathfinding.instance && Fallback(ai, state, to) && Pathfinding.instance.GetPath(from, to, path, state.Original, true, cleanup);
        }

        internal static void Fit(BaseAI ai)
        {
            var character = ai.GetComponent<Character>();
            if (!character || character.IsPlayer() || !character.IsOwner() || !Pathfinding.instance) return;
            var state = States.GetValue(ai, value => new State { Original = value.m_pathAgentType, Character = character });
            if (navigation != Pathfinding.instance)
            {
                navigation = Pathfinding.instance; profiles = (IList)Agents.GetValue(navigation);
                Profiles.Clear(); Originals.Clear();
            }
            if (ai.m_pathAgentType != state.Fitted)
                state.Original = Originals.TryGetValue(ai.m_pathAgentType, out var original) ? original : ai.m_pathAgentType;
            var data = GameFields.CharacterView(character).GetZDO();
            bool ground = !character.IsFlying() && (character.m_speed > 0f || character.m_runSpeed > 0f);
            var scale = character.transform.lossyScale;
            bool changedOwner = state.Owner != data.GetOwner() || state.OwnerRevision != data.OwnerRevision;
            if (state.Navigation == navigation && !changedOwner && state.Ground == ground && state.Scale == scale &&
                ai.m_pathAgentType == state.Fitted && Time.time < state.RefreshAt) return;
            bool changedWorld = state.Navigation != navigation;
            state.Navigation = navigation; state.Owner = data.GetOwner(); state.OwnerRevision = data.OwnerRevision;
            state.Ground = ground; state.Scale = scale; state.RefreshAt = Time.time + .25f;
            if ((int)state.Original < 0 || (int)state.Original >= profiles.Count || profiles[(int)state.Original] == null)
                throw new InvalidOperationException("The creature's original navigation profile is unavailable.");
            var native = profiles[(int)state.Original]; var nativeBuild = BuildOf(native);
            var selected = state.Original;
            float reach = nativeBuild.agentRadius;
            if (ground && CanWalk(native))
            {
                var body = CreatureClearance.Measure(character);
                float nativeDepth = SwimDepth(native);
                float bodyDepth = character.m_canSwim && CanSwim(native) && !AvoidWater(native)
                    ? Mathf.Max(0f, character.m_swimDepth - body.Bottom) : nativeDepth;
                if (body.Valid && (Mathf.Abs(body.HalfWidth - nativeBuild.agentRadius) > .01f || Mathf.Abs(body.Height - nativeBuild.agentHeight) > .01f ||
                    Mathf.Abs(bodyDepth - nativeDepth) > .01f))
                {
                    int radius = Mathf.CeilToInt((body.HalfWidth + .02f) * 10f);
                    reach = radius / 10f;
                    int swimDepth = Mathf.CeilToInt(bodyDepth * 10f);
                    // The water mesh represents the bottom of the real swimming body. Its depth bucket extends
                    // downwards; include that extra span in height so the whole physical body remains enclosed.
                    int height = Mathf.CeilToInt((body.Height + .02f + swimDepth / 10f - bodyDepth) * 4f);
                    var key = (state.Original, radius, height, swimDepth);
                    if (!Profiles.TryGetValue(key, out selected))
                    {
                        selected = (Pathfinding.AgentType)profiles.Count;
                        var profile = AddAgent.Invoke(navigation, new[] { (object)selected, native });
                        foreach (var rule in Rules) rule.SetValue(profile, rule.GetValue(native));
                        Rules[3].SetValue(profile, swimDepth / 10f);
                        var build = (NavMeshBuildSettings)Build.GetValue(profile);
                        build.agentRadius = radius / 10f; build.agentHeight = height / 4f;
                        Build.SetValue(profile, build); Profiles.Add(key, selected); Originals.Add(selected, state.Original);
                    }
                }
            }
            if (ai.m_pathAgentType != selected || changedOwner || changedWorld)
            {
                ai.m_pathAgentType = selected; Path(ai).Clear(); LastPathTime(ai) = float.NegativeInfinity;
            }
            state.Fitted = selected;
            state.Reach = reach;
        }
    }
}
