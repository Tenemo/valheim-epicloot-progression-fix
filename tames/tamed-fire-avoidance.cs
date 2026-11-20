using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The game's fire avoidance is a wild animal's fear of a fire's whole warm area, and it skips tames, so tamed animals
    // walked through lit campfires, hearths, bonfires and braziers and stood in them. A tame now keeps its body out of the
    // burning part alone, the area that hurts: it picks no wandering spot there, stops at the edge when the player, enemy or
    // food it heads for is inside, walks around one in its way and steps out of one it stands in. It neither flees nor
    // takes fright, and keeps its target and its orders. Ridden and sitting tames go where their rider or order puts them.
    [HarmonyPatch]
    internal static class TamedFireAvoidance
    {
        // How far the body, turned any way, stays from the burning area: it steps out once it touches it and walks on once
        // this far out, routes keep this far, and destinations this far.
        private const float SteppedOut = .5f, RouteClearance = .5f, DestinationClearance = .75f;
        // How far ahead a route is checked, in steps of this, and how far from a fire a tame starts looking at it.
        private const float LookAhead = 2.5f, SampleStep = .25f, NearDistance = 10f;
        // In seconds of current speed: how far ahead a route is checked, how far a leap carries and how far a stop slides.
        private const float ReactSeconds = .5f, LeapSeconds = 1f, SlideSeconds = .15f;
        // How far an attack carries the body on past where it meets its enemy.
        private const float LungePush = 1f;
        private const float DetourDegrees = 20f;
        private const int DetourSteps = 6;

        // The game lists every burning area that has woken: lit fires, and fires that have since gone out.
        private static readonly List<KeyValuePair<Bounds, EffectArea>> BurningAreas =
            (List<KeyValuePair<Bounds, EffectArea>>)(AccessTools.Field(typeof(EffectArea), "s_BurningAreas")?.GetValue(null) ??
                throw new InvalidOperationException("The game's list of burning areas changed."));
        private static readonly AccessTools.FieldRef<EffectArea, Collider> AreaCollider = AccessTools.FieldRefAccess<EffectArea, Collider>("m_collider");
        private static readonly AccessTools.FieldRef<BaseAI, List<Vector3>> Path = AccessTools.FieldRefAccess<BaseAI, List<Vector3>>("m_path");
        private static readonly AccessTools.FieldRef<BaseAI, float> JumpTimer = AccessTools.FieldRefAccess<BaseAI, float>("m_jumpTimer");

        // A burning area as a standing circle or an upright box on the ground plane, between two heights.
        private struct Hazard
        {
            internal Vector3 Center, Right, Forward;
            internal float Radius, HalfRight, HalfForward, Bottom, Top, Extent;
            internal bool Box;
            internal Collider Area;
        }

        private static readonly List<Hazard> Hazards = new List<Hazard>();
        // The fires near the creature a hook is working on, which every distance below reads.
        private static readonly List<Hazard> Close = new List<Hazard>();
        private static float hazardsRead = float.NegativeInfinity;

        private sealed class State
        {
            internal float Radius, Bottom, Top, CheckAt, SideUntil, ExitCheckAt, ExitClearance, PartsAt, Seen = float.NegativeInfinity, Floor = float.NegativeInfinity;
            internal int Side;
            internal bool Tame, Escaping;
            internal Vector3 Exit, Scale;
            // The solid parts a fire can touch.
            internal readonly List<Collider> Parts = new List<Collider>();
        }
        private static readonly ConditionalWeakTable<BaseAI, State> States = new ConditionalWeakTable<BaseAI, State>();

        // The destination of the MoveTo call in progress, so a step towards it doesn't look past it.
        [ThreadStatic] private static BaseAI moving;
        [ThreadStatic] private static Vector3 movingTo;
        [ThreadStatic] private static bool steppingOut;
        // The tame whose step the MoveTowards in progress takes.
        [ThreadStatic] private static State stepping;

        // Fires are lit, put out and built a few times a minute; reading them four times a second is enough.
        private static void ReadHazards()
        {
            if (Time.time - hazardsRead < .25f) return;
            hazardsRead = Time.time;
            Hazards.Clear();
            foreach (var pair in BurningAreas)
            {
                var area = pair.Value;
                if (!area || !area.isActiveAndEnabled) continue;
                var collider = AreaCollider(area);
                if (!collider || !collider.enabled) continue;
                var hazard = Shape(collider);
                hazard.Area = collider;
                Hazards.Add(hazard);
            }
        }

        private static Hazard Shape(Collider collider)
        {
            var transform = collider.transform;
            var scale = transform.lossyScale;
            bool upright = Vector3.Dot(transform.up, Vector3.up) > .98f;
            var hazard = new Hazard();
            if (collider is SphereCollider sphere)
            {
                hazard.Center = transform.TransformPoint(sphere.center);
                hazard.Radius = sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                hazard.Bottom = hazard.Center.y - hazard.Radius; hazard.Top = hazard.Center.y + hazard.Radius;
            }
            else if (collider is CapsuleCollider capsule && capsule.direction == 1 && upright)
            {
                hazard.Center = transform.TransformPoint(capsule.center);
                hazard.Radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                float half = Mathf.Max(capsule.height * Mathf.Abs(scale.y) / 2f, hazard.Radius);
                hazard.Bottom = hazard.Center.y - half; hazard.Top = hazard.Center.y + half;
            }
            else if (collider is BoxCollider box && upright)
            {
                hazard.Box = true;
                hazard.Center = transform.TransformPoint(box.center);
                hazard.Right = Flat(transform.right); hazard.Forward = Flat(transform.forward);
                hazard.HalfRight = box.size.x * Mathf.Abs(scale.x) / 2f; hazard.HalfForward = box.size.z * Mathf.Abs(scale.z) / 2f;
                float half = box.size.y * Mathf.Abs(scale.y) / 2f;
                hazard.Bottom = hazard.Center.y - half; hazard.Top = hazard.Center.y + half;
            }
            else
            {
                // Any other shape counts as the box around it.
                var bounds = collider.bounds;
                hazard.Box = true; hazard.Center = bounds.center; hazard.Right = Vector3.right; hazard.Forward = Vector3.forward;
                hazard.HalfRight = bounds.extents.x; hazard.HalfForward = bounds.extents.z;
                hazard.Bottom = bounds.min.y; hazard.Top = bounds.max.y;
            }
            hazard.Extent = hazard.Box ? new Vector2(hazard.HalfRight, hazard.HalfForward).magnitude : hazard.Radius;
            return hazard;
        }

        private static Vector3 Flat(Vector3 direction)
        {
            direction.y = 0f;
            return direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.zero;
        }

        // Ground distance from a point to the burning area, negative inside; infinite where the heights don't meet.
        private static float Distance(in Hazard hazard, Vector3 point, float bottom, float top)
        {
            if (top < hazard.Bottom || bottom > hazard.Top) return float.PositiveInfinity;
            var offset = point - hazard.Center; offset.y = 0f;
            if (!hazard.Box) return offset.magnitude - hazard.Radius;
            float right = Mathf.Abs(Vector3.Dot(offset, hazard.Right)) - hazard.HalfRight;
            float forward = Mathf.Abs(Vector3.Dot(offset, hazard.Forward)) - hazard.HalfForward;
            return new Vector2(Mathf.Max(right, 0f), Mathf.Max(forward, 0f)).magnitude + Mathf.Min(Mathf.Max(right, forward), 0f);
        }

        // How far the body, turned any way where it stands, is from the nearest burning area, and which one that is.
        // A creature turns where it stands, so its head or tail can reach anywhere around it.
        private static float Clearance(State state, Vector3 point) => Clearance(state, point, out _);

        // A climb or drop of this much either way counts too.
        private static float Clearance(State state, Vector3 point, out int nearest, float climb = 0f)
        {
            nearest = -1;
            // On a slope a long body's head or tail hangs below its feet when it faces downhill.
            float lowest = float.PositiveInfinity, bottom = point.y + state.Bottom - state.Radius * .5f - climb, top = point.y + state.Top + climb;
            for (int index = 0; index < Close.Count; index++)
            {
                float clearance = Distance(Close[index], point, bottom, top) - state.Radius;
                if (clearance < lowest) { lowest = clearance; nearest = index; }
            }
            return lowest;
        }

        // A tame its own game runs, on foot and free to go, with a lit fire within reach. Every creature's movement asks
        // this, so a wild creature costs one cached flag and a tame far from fire one look at the lit fires.
        private static State Near(BaseAI ai, Character character)
        {
            // One AI step asks this from several hooks; the answer holds for the step.
            if (ai == lastAi && Time.time == lastAt) return lastState;
            lastAi = ai; lastAt = Time.time;
            return lastState = Find(ai, character);
        }

        [ThreadStatic] private static BaseAI lastAi;
        [ThreadStatic] private static float lastAt;
        [ThreadStatic] private static State lastState;

        private static State Find(BaseAI ai, Character character)
        {
            if (!character || !character.IsTamed()) return null;
            ReadHazards();
            if (Hazards.Count == 0) return null;
            var position = character.transform.position;
            Close.Clear();
            foreach (var hazard in Hazards)
            {
                float reach = hazard.Extent + NearDistance;
                float x = position.x - hazard.Center.x, z = position.z - hazard.Center.z;
                if (x * x + z * z < reach * reach) Close.Add(hazard);
            }
            if (Close.Count == 0 || !character.IsOwner() || character.IsDead() || character.HaveRider() || character.IsFlying() || character.IsSwimming() || ai.IsSleeping()) return null;
            var state = States.GetOrCreateValue(ai);
            // A step out stops while the tame is away from fires, ridden, swimming or asleep.
            if (Time.time - state.Seen > .5f) { state.Escaping = false; state.Exit = Vector3.zero; state.Floor = float.NegativeInfinity; }
            state.Seen = Time.time;
            if (Time.time >= state.CheckAt)
            {
                state.CheckAt = Time.time + 1f;
                state.Tame = Companions.IsTame(character) && !TameRiding.HoldingPosition(character);
                if (state.Tame && !Measure(state, character)) state.Tame = false;
            }
            return state.Tame ? state : null;
        }

        private static readonly int BurningLayer = LayerMask.NameToLayer("character_trigger");
        private static readonly List<Collider> Parts = new List<Collider>();

        // Real contact between the body and a nearby fire, and which fire.
        private static bool Touches(State state, out int touched)
        {
            for (touched = 0; touched < Close.Count; touched++)
            {
                var area = Close[touched].Area;
                if (!area) continue;
                var bounds = area.bounds;
                foreach (var part in state.Parts)
                    if (part && part.enabled && part.bounds.Intersects(bounds) && Physics.ComputePenetration(part, part.transform.position, part.transform.rotation,
                        area, area.transform.position, area.transform.rotation, out _, out _)) return true;
            }
            touched = -1;
            return false;
        }

        // The body as a fire reaches it: every solid part its burning area can touch, such as a lox's head and trunk, not only
        // the parts that bump into walls. How far its farthest part reaches from where it stands, and its heights.
        private static bool Measure(State state, Character character)
        {
            var origin = character.transform.position;
            float reach = 0f, bottom = float.PositiveInfinity, top = float.NegativeInfinity;
            void Include(Vector3 point, float radius)
            {
                var offset = point - origin;
                reach = Mathf.Max(reach, new Vector2(offset.x, offset.z).magnitude + radius);
                bottom = Mathf.Min(bottom, offset.y - radius); top = Mathf.Max(top, offset.y + radius);
            }
            // Which parts these are changes only with a saddle or the like; looking through every bone takes long.
            if (Time.time >= state.PartsAt)
            {
                state.PartsAt = Time.time + 10f;
                character.GetComponentsInChildren(Parts);
                state.Parts.Clear();
                foreach (var part in Parts)
                    if (part.enabled && !part.isTrigger && part.gameObject.activeInHierarchy && !Physics.GetIgnoreLayerCollision(part.gameObject.layer, BurningLayer))
                        state.Parts.Add(part);
                Parts.Clear();
            }
            foreach (var part in state.Parts)
            {
                if (!part || !part.enabled) continue;
                var transform = part.transform;
                var scale = transform.lossyScale; scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                if (part is CapsuleCollider capsule)
                {
                    int axis = capsule.direction;
                    float radius = capsule.radius * Mathf.Max(scale[(axis + 1) % 3], scale[(axis + 2) % 3]);
                    var along = transform.TransformDirection(axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward).normalized *
                        Mathf.Max(0f, capsule.height * scale[axis] / 2f - radius);
                    var center = transform.TransformPoint(capsule.center);
                    Include(center - along, radius); Include(center + along, radius);
                }
                else if (part is SphereCollider sphere) Include(transform.TransformPoint(sphere.center), sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)));
                else if (part is BoxCollider box)
                {
                    for (int corner = 0; corner < 8; corner++)
                        Include(transform.TransformPoint(box.center + Vector3.Scale(box.size / 2f,
                            new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f))), 0f);
                }
                else
                {
                    var bounds = part.bounds;
                    for (int corner = 0; corner < 8; corner++)
                        Include(bounds.center + Vector3.Scale(bounds.extents, new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f)), 0f);
                }
            }
            if (reach <= 0f) return false;
            // A head, trunk or tail swings as the creature moves, so the widest reach measured holds, unless its size changed.
            var size = character.transform.lossyScale;
            if (size != state.Scale) { state.Scale = size; state.Radius = 0f; state.Bottom = float.PositiveInfinity; state.Top = float.NegativeInfinity; }
            state.Radius = Mathf.Max(state.Radius, reach); state.Bottom = Mathf.Min(state.Bottom, bottom); state.Top = Mathf.Max(state.Top, top);
            return true;
        }

        // Whether walking this way brings the body closer to a fire than it may come, within this distance. A long body
        // turns along its way, so its whole turning circle keeps clear: a lox's head swings round as far as its length.
        private static bool Blocked(State state, Vector3 position, Vector3 direction, float distance, out int nearest)
        {
            float limit = Limit(state, Clearance(state, position, out nearest));
            // The way may climb or drop as it goes, as on a slope of up to 35 degrees; the same allowance along all of it keeps
            // clearance changing by at most the distance moved, so the way is clear for as far as the margin over the limit.
            float climb = Mathf.Min(distance, 1.5f) * .7f;
            float clearance = Clearance(state, position, out _, climb);
            for (float travelled = 0f; travelled < distance; )
            {
                travelled = Mathf.Min(distance, travelled + Mathf.Max(clearance - limit, SampleStep));
                clearance = Clearance(state, position + direction * travelled, out int hit, climb);
                if (clearance < limit) { nearest = hit; return true; }
            }
            return false;
        }

        // How close the tame's own steps may bring it to a fire: the route's margin, or, once inside it, no closer than it has
        // been since, so it can't creep in step by step. Being pushed deeper sets a new floor.
        private static float Limit(State state, float clearance)
        {
            if (clearance >= RouteClearance || clearance < state.Floor - .1f) state.Floor = Mathf.Min(RouteClearance, clearance);
            else if (clearance > state.Floor) state.Floor = clearance;
            // A little slack so rounding doesn't stop it, never so much that a body clear of the fire reaches into it.
            return state.Floor >= 0f ? Mathf.Max(0f, state.Floor - .02f) : state.Floor - .02f;
        }

        // A destination in a fire moves out to its edge on the creature's side.
        [HarmonyPatch(typeof(BaseAI), "MoveTo"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void KeepDestinationOut(BaseAI __instance, Character ___m_character, ref Vector3 point)
        {
            moving = null;
            var state = Near(__instance, ___m_character);
            if (state == null) return;
            moving = __instance;
            // A running creature slides on as it stops, so it stops further out.
            var velocity = ___m_character.GetVelocity(); velocity.y = 0f;
            float margin = DestinationClearance + velocity.magnitude * SlideSeconds;
            float clearance = Clearance(state, point);
            if (clearance < margin)
            {
                var position = __instance.transform.position;
                var outwards = Flat(position - point);
                if (outwards == Vector3.zero) outwards = Flat(-__instance.transform.forward);
                // Clearance grows by at most the distance moved, so each step covers what is still missing.
                for (float step = 0f; step <= 12f; )
                {
                    step += Mathf.Max(SampleStep, margin - clearance);
                    clearance = Clearance(state, point + outwards * step);
                    if (clearance >= margin) { point += outwards * step; break; }
                }
            }
            movingTo = point;
        }

        [HarmonyPatch(typeof(BaseAI), "MoveTo"), HarmonyFinalizer]
        private static Exception EndMove(Exception __exception) { moving = null; return __exception; }

        // A tame doesn't pick a spot to wander to in a fire.
        [HarmonyPatch(typeof(BaseAI), "IsValidRandomMovePoint"), HarmonyPostfix]
        private static void RefuseFireSpot(BaseAI __instance, Character ___m_character, Vector3 point, ref bool __result)
        {
            if (!__result) return;
            var state = Near(__instance, ___m_character);
            if (state != null && Clearance(state, point) < DestinationClearance) __result = false;
        }

        // Every step a creature takes on foot comes here. A tame's step into a fire turns aside, and with no way around, it waits.
        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.MoveTowards)), HarmonyPrefix]
        private static bool Steer(BaseAI __instance, Character ___m_character, ref Vector3 dir, bool run)
        {
            stepping = null;
            if (steppingOut) return true;
            var state = Near(__instance, ___m_character);
            if (state == null) return true;
            stepping = state;
            var direction = Flat(dir);
            if (direction == Vector3.zero) return true;
            var position = __instance.transform.position;
            var velocity = ___m_character.GetVelocity(); velocity.y = 0f;
            float speed = velocity.magnitude;
            // A creature that leaps as it runs, such as an asksvin, waits to leap until its leap won't carry it through a fire.
            if (__instance.m_jumpInterval > 0f && JumpTimer(__instance) >= __instance.m_jumpInterval &&
                Blocked(state, position, speed > .5f ? velocity / speed : direction, Mathf.Max(LookAhead, speed * LeapSeconds), out _))
                JumpTimer(__instance) = __instance.m_jumpInterval * .99f;
            // A fast creature looks further ahead, so it turns in time.
            float distance = Mathf.Max(LookAhead, speed * ReactSeconds);
            if (moving == __instance)
            {
                // A route corner inside a fire can't be reached; the next one can.
                var path = Path(__instance);
                while (path.Count > 1 && Clearance(state, path[0]) < RouteClearance && Vector3.Angle(direction, Flat(path[0] - position)) < 5f)
                {
                    path.RemoveAt(0);
                    direction = Flat(path[0] - position);
                    if (direction == Vector3.zero) return true;
                    dir = direction;
                }
                var target = path.Count > 0 && Vector3.Angle(direction, Flat(path[0] - position)) < 5f ? path[0] : movingTo;
                if (Vector3.Angle(direction, Flat(target - position)) < 5f) distance = Mathf.Min(distance, Utils.DistanceXZ(target, position));
            }
            if (!Blocked(state, position, direction, distance, out int nearest)) return true;
            if (Time.time >= state.SideUntil || state.Side == 0)
            {
                var hazard = nearest >= 0 ? Close[nearest].Center : position + direction;
                state.Side = Vector3.Cross(direction, hazard - position).y > 0f ? -1 : 1;
            }
            state.SideUntil = Time.time + 2f;
            float lookAhead = Mathf.Max(distance, 1f);
            var center = ___m_character.GetCenterPoint();
            for (int step = 1; step <= DetourSteps; step++)
                for (int turn = 0; turn < 2; turn++)
                {
                    int side = turn == 0 ? state.Side : -state.Side;
                    var candidate = Quaternion.Euler(0f, side * step * DetourDegrees, 0f) * direction;
                    if (Blocked(state, position, candidate, lookAhead, out _) || __instance.Raycast(center, candidate, 1.5f, .1f) < 1.5f) continue;
                    state.Side = side;
                    dir = candidate;
                    return true;
                }
            stepping = null;
            __instance.StopMoving();
            return false;
        }

        // A creature with a wide turn walks on along its facing while it turns. A step that would still bring the body
        // closer to a fire than it may come waits until it has turned.
        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.MoveTowards)), HarmonyPostfix]
        private static void GuardStep(BaseAI __instance, Character ___m_character)
        {
            var state = stepping;
            stepping = null;
            if (state == null || !___m_character) return;
            var move = Flat(GameFields.MoveDirection(___m_character));
            if (move == Vector3.zero) return;
            var position = __instance.transform.position;
            float now = Clearance(state, position);
            if (now > RouteClearance + 1f) return;
            if (Clearance(state, position + move * .5f) < Limit(state, now)) ___m_character.SetMoveDir(Vector3.zero);
        }

        // An attack's lunge carries the body up to its enemy and on into it, so a tame waits beside a fire for an enemy
        // standing in or at it.
        [HarmonyPatch(typeof(MonsterAI), "DoAttack"), HarmonyPrefix]
        private static bool HoldAttack(MonsterAI __instance, Character ___m_character, Character target, ref bool __result)
        {
            if (!target) return true;
            var state = Near(__instance, ___m_character);
            if (state == null) return true;
            var position = __instance.transform.position;
            var direction = Flat(target.transform.position - position);
            if (direction == Vector3.zero) return true;
            float distance = Utils.DistanceXZ(target.transform.position, position);
            float lunge = Mathf.Min(distance, Mathf.Max(0f, distance - target.GetRadius() - state.Radius) + LungePush);
            if (!Blocked(state, position, direction, lunge, out _)) return true;
            __instance.StopMoving();
            __result = false;
            return false;
        }

        // After the AI has decided, a tame touching a fire steps out of it, away from the fire and clear of walls.
        [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.UpdateAI)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void StepOut(MonsterAI __instance, Character ___m_character, bool __result)
        {
            if (!__result) return;
            var state = Near(__instance, ___m_character);
            if (state == null) return;
            var position = __instance.transform.position;
            var facing = Flat(__instance.transform.forward);
            float clearance = Clearance(state, position, out int nearest);
            // It steps out once any part of it touches the flames, and is out once it can turn round where it stands
            // without its head or tail reaching back in. Only a body near the edge is checked for contact.
            if (!state.Escaping && clearance < DestinationClearance && Touches(state, out int touched)) { state.Escaping = true; nearest = touched; }
            else if (state.Escaping && clearance >= SteppedOut) state.Escaping = false;
            if (!state.Escaping) { state.Exit = Vector3.zero; return; }
            if (nearest < 0 || ___m_character.InAttack()) return;
            // The way out holds while it gets the body further out, so the creature isn't left turning between two ways.
            bool starting = state.Exit == Vector3.zero;
            if (starting || Time.time >= state.ExitCheckAt && clearance < state.ExitClearance + .05f)
                state.Exit = ExitDirection(__instance, state, position, facing, clearance, Close[nearest]);
            if (starting || Time.time >= state.ExitCheckAt) { state.ExitCheckAt = Time.time + 1f; state.ExitClearance = clearance; }
            // At a walk: a running lox or asksvin tramples the fireplace it stands in.
            steppingOut = true;
            try { __instance.MoveTowards(state.Exit, false); }
            finally { steppingOut = false; }
            // It turns before it walks while walking on its facing would take it deeper.
            var move = Flat(GameFields.MoveDirection(___m_character));
            if (move != Vector3.zero && Clearance(state, position + move * .5f) < clearance - .01f) ___m_character.SetMoveDir(Vector3.zero);
        }

        // Out of the fire with as little turning as possible: a creature walks only once it faces its way. Any way that takes
        // the body further out of the fire and isn't blocked by a wall will do, the one nearest its facing first.
        private static Vector3 ExitDirection(BaseAI ai, State state, Vector3 position, Vector3 facing, float clearance, Hazard hazard)
        {
            var offset = position - hazard.Center; offset.y = 0f;
            var away = offset.magnitude > .2f ? offset.normalized : facing;
            if (hazard.Box)
            {
                float right = Vector3.Dot(offset, hazard.Right), forward = Vector3.Dot(offset, hazard.Forward);
                float rightGap = hazard.HalfRight - Mathf.Abs(right), forwardGap = hazard.HalfForward - Mathf.Abs(forward);
                // Inside a box, its nearest side.
                if (rightGap > 0f && forwardGap > 0f)
                    away = rightGap < forwardGap ? hazard.Right * (right < 0f ? -1f : 1f) : hazard.Forward * (forward < 0f ? -1f : 1f);
            }
            if (away == Vector3.zero) away = Vector3.forward;
            var center = GameFields.AiCharacter(ai).GetCenterPoint();
            var best = Vector3.zero; float bestAngle = float.PositiveInfinity;
            for (int step = -3; step <= 3; step++)
            {
                var candidate = Quaternion.Euler(0f, step * 30f, 0f) * away;
                float angle = facing == Vector3.zero ? 0f : Vector3.Angle(candidate, facing);
                if (angle >= bestAngle || ai.Raycast(center, candidate, 1.5f, .1f) < 1.5f) continue;
                if (Clearance(state, position + candidate) < clearance + .1f) continue;
                best = candidate; bestAngle = angle;
            }
            return best != Vector3.zero ? best : away;
        }
    }
}
