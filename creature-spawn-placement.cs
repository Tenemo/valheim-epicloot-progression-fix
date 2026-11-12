using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static partial class CreatureSpawnPlacement
    {
        private const string PendingKey = Plugin.Id + ".spawn-placement-pending";
        private const string DiscardKey = Plugin.Id + ".spawn-placement-discard";
        private const string AreaKey = Plugin.Id + ".spawn-placement-area";
        private const string SummonKey = Plugin.Id + ".spawn-placement-summon";
        // How far a creature looks for a clear spot once its wait is over.
        private const float WideSearch = 8f;
        private delegate bool PointCheck(ref Vector3 point);
        private delegate bool NaturalCheck(SpawnSystem system, SpawnSystem.SpawnData definition, ref Vector3 point);
        private static readonly NaturalCheck CheckNatural = AccessTools.MethodDelegate<NaturalCheck>(AccessTools.Method(typeof(SpawnSystem), "IsSpawnPointGood"));
        private static readonly Action<Tameable, int> EnforceSummonLimit = AccessTools.MethodDelegate<Action<Tameable, int>>(AccessTools.Method(typeof(Tameable), "UnsummonMaxInstances"));
        private static readonly AccessTools.FieldRef<BaseAI, Vector3> SpawnPoint = AccessTools.FieldRefAccess<BaseAI, Vector3>("m_spawnPoint");
        private static readonly Action<BaseAI, Vector3> SetPatrolPoint = AccessTools.MethodDelegate<Action<BaseAI, Vector3>>(AccessTools.Method(typeof(BaseAI), "SetPatrolPoint", new[] { typeof(Vector3) }));
        private static readonly Dictionary<Character, Entry> Pending = new();
        private static readonly ConditionalWeakTable<SpawnSystem, NaturalArea> NaturalAreas = new();
        private static readonly ConditionalWeakTable<Character, Fallback> Fallbacks = new();
        private static readonly List<Piece> NearbyPieces = new();
        [ThreadStatic] private static Scope current;
        private static ManualLogSource logger;

        private sealed class NaturalArea
        {
            internal SpawnSystem.SpawnData Definition;
            internal Vector3 Center, Player;
            internal int Frame;
        }
        private sealed class Scope
        {
            internal Vector3 Center;
            internal float Radius, Offset;
            internal PointCheck Check, Area;
            internal SpawnAbility Ability;
            internal Character Parent;
            internal string Description;
            // A player's summon always appears: when nothing nearby is clear it takes the best supported spot, or its own.
            internal bool Closed, Resolving, Failed, Discard, Reported, Summon, OutdoorEncounter;
            internal float NextAttempt;
            internal readonly List<Entry> Entries = new();
        }
        private sealed class Call { internal Scope Previous, Active; }
        private sealed class Entry
        {
            internal Character Character;
            internal Scope Scope;
            internal Vector3 Origin, Center;
            internal float Radius, Offset;
            internal PointCheck Check;
            internal bool Ready, Done, Accepted, TimedOut;
            internal GameObject Aoe;
        }
        private sealed class Fallback { internal float NextCheck; internal bool Reported; }

        internal static void Initialize(IntegrationPatches patches, ManualLogSource log)
        {
            logger = log;
            var type = typeof(CreatureSpawnPlacement);
            void Factory(Type owner, string name, string before) => patches.Patch(AccessTools.Method(owner, name),
                prefix: new HarmonyMethod(type, before) { priority = Priority.Last }, finalizer: new HarmonyMethod(type, nameof(AfterFactory)));
            patches.Patch(AccessTools.Method(typeof(Character), "Awake"), prefix: new HarmonyMethod(type, nameof(Capture)) { priority = Priority.Last });
            patches.Patch(AccessTools.Method(typeof(SpawnSystem), "FindBaseSpawnPoint"), postfix: new HarmonyMethod(type, nameof(NaturalCenter)));
            Factory(typeof(SpawnSystem), "Spawn", nameof(BeforeNatural));
            Factory(typeof(CreatureSpawner), "Spawn", nameof(BeforeFixed));
            Factory(typeof(SpawnArea), "SpawnOne", nameof(BeforeArea));
            Factory(typeof(Procreation), "Procreate", nameof(BeforeBirth));
            patches.Patch(AccessTools.Method(typeof(Growup), "GrowUpdate"), transpiler: new HarmonyMethod(type, nameof(GrowthPosition)));
            InitializeAdditionalRoutes(patches);
            patches.Patch(AccessTools.Method(typeof(Tameable), "UnsummonMaxInstances"), prefix: new HarmonyMethod(type, nameof(KeepPreviousSummons)));
            patches.Patch(AccessTools.Method(typeof(Tameable), "UnsummonMaxInstances"), transpiler: new HarmonyMethod(type, nameof(CountPlacedSummons)));
            patches.Patch(AccessTools.Method(typeof(Character), nameof(Character.Damage)), prefix: new HarmonyMethod(type, nameof(AfterPlacementOnly)));
            patches.Patch(AccessTools.Method(typeof(Character), "RPC_Damage"), prefix: new HarmonyMethod(type, nameof(AfterPlacementOnly)));
        }

        private static Call Enter(Scope scope)
        {
            var call = new Call { Previous = current, Active = scope }; current = scope; return call;
        }

        private static Exception AfterFactory(Exception __exception, Call __state)
        {
            if (__state == null) return __exception;
            current = __state.Previous; var scope = __state.Active;
            scope.Closed = true;
            if (__exception != null) Reject(scope);
            else if (!StarLevelSystem.Installed)
            {
                foreach (var entry in scope.Entries) entry.Ready = true;
                Resolve(scope);
            }
            return __exception;
        }

        private static void Capture(Character __instance)
        {
            if (__instance is Player || ZNetView.m_useInitZDO || !__instance.GetComponent<BaseAI>()) return;
            var view = __instance.GetComponent<ZNetView>();
            if (!view || !view.IsValid() || !view.IsOwner()) return;
            // Native boss offerings, eggs, growth and mod-created replacements have already selected their spawn position.
            // A blocked one moves within its own size on the same floor and side of any wall, or keeps its identity until
            // its wait ends; these one-shot producers must not lose it to a moment's crowding.
            var origin = AboveGround(__instance.transform.position);
            var scope = current ?? new Scope { Center = origin, Radius = float.PositiveInfinity, Closed = true,
                Description = "creature spawn", Check = SameArea(origin) };
            if (scope.Ability) ConfigureAbility(scope, origin);
            var entry = new Entry { Character = __instance, Scope = scope, Origin = origin,
                Center = scope.Center, Radius = scope.Radius, Offset = scope.Offset, Check = scope.Check };
            scope.Entries.Add(entry); Pending.Add(__instance, entry);
            view.GetZDO().Set(PendingKey, true); view.GetZDO().Set(DiscardKey, scope.Discard); view.GetZDO().Set(SummonKey, scope.Summon);
        }

        private static void NaturalCenter(SpawnSystem __instance, SpawnSystem.SpawnData spawn, Vector3 spawnCenter, Player targetPlayer, bool __result)
        {
            if (!__result || !targetPlayer) return;
            var area = NaturalAreas.GetOrCreateValue(__instance);
            area.Definition = spawn; area.Center = spawnCenter; area.Player = targetPlayer.transform.position; area.Frame = Time.frameCount;
        }

        private static void BeforeNatural(SpawnSystem __instance, SpawnSystem.SpawnData critter, Vector3 spawnPoint, out Call __state)
        {
            Vector3 ground = spawnPoint; CheckNatural(__instance, critter, ref ground);
            float offset = spawnPoint.y - ground.y;
            Vector3 center = ground; float radius = 4f;
            bool known = NaturalAreas.TryGetValue(__instance, out var area) && area.Frame == Time.frameCount && ReferenceEquals(area.Definition, critter);
            Vector3 selectedPlayer = known ? area.Player : Vector3.zero;
            if (known && critter.m_groupRadius > 0f) { center = area.Center; radius = critter.m_groupRadius; }
            Vector3 origin = ground; var zone = ZoneSystem.GetZone(origin);
            PointCheck check = (ref Vector3 point) =>
            {
                if (ZoneSystem.GetZone(point) != zone || !CheckNatural(__instance, critter, ref point)) return false;
                if (!known || critter.m_groupRadius > 0f) return true;
                float distance = Utils.DistanceXZ(point, selectedPlayer);
                return distance >= (critter.m_spawnRadiusMin > 0f ? critter.m_spawnRadiusMin : 40f) &&
                    distance <= (critter.m_spawnRadiusMax > 0f ? critter.m_spawnRadiusMax : 80f);
            };
            __state = Enter(new Scope { Center = center, Radius = radius, Offset = offset, Check = check, Discard = true, Description = "natural or raid spawn" });
        }

        private static PointCheck LocalGround(Component source, Vector3 origin)
        {
            var sameArea = SameArea(origin, source);
            return (ref Vector3 point) =>
            {
                if (!sameArea(ref point) || !ZoneSystem.instance.FindFloor(point, out float height)) return false;
                point.y = height; return true;
            };
        }

        private static void BeforeFixed(CreatureSpawner __instance, out Call __state)
        {
            var origin = __instance.transform.position;
            if (ZoneSystem.instance.FindFloor(origin, out float floor)) origin.y = floor;
            __state = Enter(new Scope { Center = origin, Radius = float.PositiveInfinity, Description = "fixed encounter", Check = LocalGround(__instance, origin) });
        }

        private static void BeforeArea(SpawnArea __instance, out Call __state)
        {
            var origin = __instance.transform.position;
            var ground = LocalGround(__instance, origin); bool groundOnly = __instance.m_onGroundOnly;
            PointCheck check = (ref Vector3 point) => ground(ref point) && (!groundOnly || !ZoneSystem.instance.IsBlocked(point));
            __state = Enter(new Scope { Center = origin, Radius = __instance.m_spawnRadius, Offset = .1f, Check = check, Discard = true, Description = "area spawn" });
        }

        private static void BeforeBirth(Procreation __instance, out Call __state)
        {
            var origin = __instance.transform.position;
            // The native offset chooses a starting point, not the dimensions of the parent or offspring. Keep a
            // nearby birth inside the same area counted by the existing population limit, never outside that limit.
            float radius = __instance.m_totalCheckRange > 0f ? __instance.m_totalCheckRange : float.PositiveInfinity;
            __state = Enter(new Scope { Center = origin, Radius = radius, Parent = __instance.GetComponent<Character>(),
                Check = LocalGround(__instance, origin), Description = "offspring" });
        }

        // A young animal's pivot can lie inside its body: a large moose calf's is about a metre below its hooves. The grown
        // animal starts where the young one stands, the level its placement looks for a floor at, instead of in the ground.
        private static IEnumerable<CodeInstruction> GrowthPosition(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.position))),
                AccessTools.Method(typeof(CreatureSpawnPlacement), nameof(Feet)), 1, "The native growth position changed.");

        private static Vector3 Feet(Transform young)
        {
            var position = young.position;
            var character = young.GetComponent<Character>();
            var size = character ? CreatureClearance.Measure(character, true) : default;
            if (size.Valid) position.y += size.Bottom;
            return position;
        }

        // A spawner the terrain has buried creates its creature under the ground. The game lifts a creature found more than
        // 1 m down onto the ground above it, but one waiting to appear is not simulated, so it starts from that ground here.
        private static Vector3 AboveGround(Vector3 point)
        {
            float ground = ZoneSystem.instance.GetGroundHeight(point);
            if (point.y < ground - 1f) point.y = ground;
            return point;
        }

        // A dungeon room can bury a spawner in the solid block of its own floor, too deep for the game's floor search to see
        // the top. A creature has nothing to stand on in there, so it starts from the top of that floor. Only a convex solid has
        // an inside, and a top over 2 m up is a rock or a wall, not a floor. A ruin's pieces count; what a player built doesn't,
        // so a wall never lifts a creature onto a base.
        private static Vector3 OutOfFloor(Vector3 point)
        {
            float top = point.y;
            foreach (var solid in Physics.OverlapSphere(point, .01f, LayerMask.GetMask("Default", "static_solid", "piece"), QueryTriggerInteraction.Ignore))
                if (!(solid is MeshCollider { convex: false }) && solid.GetComponentInParent<Piece>()?.IsPlacedByPlayer() != true &&
                    solid.Raycast(new Ray(new Vector3(point.x, solid.bounds.max.y + 1f, point.z), Vector3.down), out var surface, solid.bounds.size.y + 1f))
                    top = Mathf.Max(top, surface.point.y);
            if (top > point.y + .25f && top <= point.y + 2f && ZoneSystem.instance.FindFloor(new Vector3(point.x, top, point.z), out float floor)) point.y = floor;
            return point;
        }

        // Judged where the creature ends up, the first surface below it, so a fall onto a roof counts. A player's base is where
        // the game's own spawners stay quiet, and a piece within 10 m covers a build that has lost its workbench and fires.
        private static bool NearPlayerBuild(Vector3 point)
        {
            if (Physics.Raycast(point + Vector3.up * .1f, Vector3.down, out var surface, 100f,
                LayerMask.GetMask("terrain", "Default", "static_solid", "piece", "piece_nonsolid", "vehicle"), QueryTriggerInteraction.Ignore)) point = surface.point;
            if (EffectArea.IsPointInsideArea(point, EffectArea.Type.PlayerBase)) return true;
            Piece.GetAllPiecesInRadius(point, 10f, NearbyPieces);
            bool built = NearbyPieces.Any(piece => piece.IsPlacedByPlayer());
            NearbyPieces.Clear();
            return built;
        }

        // What players came for, or raised, is never dropped for want of space.
        private static bool MustAppear(Character character) => character.IsBoss() || character.IsTamed() ||
            GameFields.CharacterView(character).GetZDO().GetString(BountyCreature.IdKey).Length > 0;

        internal static bool Ready(Character character, bool timedOut)
        {
            if (Pending.TryGetValue(character, out var entry))
            {
                entry.Ready = true; entry.TimedOut |= timedOut; Resolve(entry.Scope);
                return entry.Done && entry.Accepted;
            }
            var view = character ? GameFields.CharacterView(character) : null;
            if (!view || !view.IsValid() || !view.GetZDO().GetBool(PendingKey)) return true;
            if (!view.IsOwner()) return false;
            // Resume a saved Nemesis encounter with its outdoor search. A saved summon takes its nearest clear spot
            // on its floor and side of walls, else the best supported one, else where it is.
            bool nemesis = NemesisPlacement(view.GetZDO());
            if (view.GetZDO().GetBool(SummonKey) || nemesis)
            {
                var at = character.transform.position;
                var scope = nemesis ? NemesisScope(at) : new Scope { Center = at, Radius = float.PositiveInfinity, Closed = true, Summon = true, Description = "player summon",
                    Check = SameArea(at) };
                scope.Closed = true;
                scope.Area = scope.Check;
                var resumed = new Entry { Character = character, Scope = scope, Origin = at, Center = at, Radius = scope.Radius, Check = scope.Check,
                    Ready = true, TimedOut = true };
                scope.Entries.Add(resumed); Pending[character] = resumed;
                Resolve(scope);
                return resumed.Done && resumed.Accepted;
            }
            var fallback = Fallbacks.GetOrCreateValue(character);
            if (Time.time < fallback.NextCheck) return false;
            fallback.NextCheck = Time.time + 2f;
            Physics.SyncTransforms();
            // One saved before it appeared takes its own spot, or once its area has loaded the nearest clear one within its
            // size on its floor and side of any wall, as a new creature does, and within 8 m when its wait is over. A wild one
            // always waits for that loading, then stays away from what players have built; a tame still appears at home.
            var origin = OutOfFloor(AboveGround(character.transform.position));
            var saved = new Entry { Character = character, Scope = new Scope(), Origin = origin, Center = origin,
                Radius = float.PositiveInfinity, Check = SameArea(origin) };
            bool ready = ZNetScene.instance.IsAreaReady(origin), wild = !character.IsTamed(), discard = view.GetZDO().GetBool(DiscardKey);
            float reach = ready ? Mathf.Max(.75f, CreatureClearance.Measure(character).Radius * 2f) : 0f;
            var wider = timedOut && ready && !discard ? CreaturePlacement.Candidates(origin, WideSearch) : Enumerable.Empty<Vector3>();
            foreach (var candidate in CreaturePlacement.Candidates(origin, reach).Concat(wider))
            {
                if (!TryPoint(saved, candidate, out var position) || wild && (!ready || NearPlayerBuild(position))) continue;
                PlaceNewCreature(character, origin, position); view.GetZDO().Set(PendingKey, false);
                ReleaseArea(character, position); return true;
            }
            if (timedOut && (discard || ready && !MustAppear(character)))
            {
                FreshCreatureSpawns.Cancel(character);
                ZNetScene.instance.Destroy(character.gameObject);
                return false;
            }
            if (timedOut && ready)
            {
                var crowded = BestSpot(saved, reach);
                PlaceNewCreature(character, origin, crowded); Physics.SyncTransforms();
                CreaturePlacement.Settle(character);
                view.GetZDO().Set(PendingKey, false); ReleaseArea(character, crowded);
                logger?.LogInfo("No clear space for a saved creature; " + character.name + " appears at the best place nearby.");
                return true;
            }
            // An interrupted placement is not an ordinary saved creature. Keep its existing identity and rolled data
            // out of simulation until its wait is over; never move a saved creature onto a different floor.
            if (timedOut && !fallback.Reported)
            { fallback.Reported = true; logger?.LogWarning("An unfinished creature placement is waiting for its area to load: " + character.name); }
            return false;
        }

        private static void Resolve(Scope scope)
        {
            if (!scope.Closed || scope.Resolving || scope.Failed || Time.time < scope.NextAttempt) return;
            scope.Resolving = true;
            try
            {
                foreach (var entry in scope.Entries.Where(entry => entry.Ready && !entry.Done).ToArray())
                {
                    var character = entry.Character; var view = character ? GameFields.CharacterView(character) : null;
                    if (!view || !view.IsValid())
                    {
                        // One summon lost elsewhere, such as a bat the swarm limit removed, leaves the others to appear.
                        if (scope.Summon) { entry.Done = true; Pending.Remove(character); continue; }
                        Reject(scope); return;
                    }
                    if (!view.IsOwner())
                    {
                        if (!view.GetZDO().GetBool(PendingKey)) Complete(entry, character.transform.position);
                        continue;
                    }
                    // Another game already finished it while this one did not own it.
                    if (!view.GetZDO().GetBool(PendingKey)) { Complete(entry, character.transform.position); continue; }
                    if (!ZNetScene.instance.IsAreaReady(entry.Origin) && !(scope.Summon && entry.TimedOut)) continue;
                    Physics.SyncTransforms(); CreatureClearance.Measure(character, true);
                    bool found = TryPoint(entry, entry.Origin, out var position);
                    float search = Mathf.Max(.75f, CreatureClearance.Measure(character).Radius * 2f);
                    if (!found)
                    {
                        if (scope.Parent) search = Mathf.Max(search, CreatureClearance.Measure(scope.Parent).Radius + CreatureClearance.Measure(character).Radius);
                        foreach (var candidate in CreaturePlacement.Candidates(entry.Origin, Mathf.Min(entry.Radius, search)))
                            if (TryPoint(entry, candidate, out position)) { found = true; break; }
                        // A native fallback can lie outside a route's valid inner radius. If the nearby body-sized
                        // search cannot reach the permitted region, try that original region itself, still bounded.
                        if (!found && !float.IsInfinity(entry.Radius) &&
                            (entry.Radius > search + .01f || Utils.DistanceXZ(entry.Origin, entry.Center) > .01f))
                            foreach (var candidate in CreaturePlacement.Candidates(entry.Center, entry.Radius))
                                if (TryPoint(entry, candidate, out position)) { found = true; break; }
                    }
                    // A spawn with nowhere to go at the end of its wait no longer holds its spawner: a wild creature is removed
                    // and anything else appears crowded, as a summon does at once.
                    bool last = !found && !scope.Summon && !scope.Discard && !scope.OutdoorEncounter && entry.TimedOut;
                    if (last)
                    {
                        entry.Origin = OutOfFloor(entry.Origin);
                        foreach (var candidate in CreaturePlacement.Candidates(entry.Origin, search).Concat(CreaturePlacement.Candidates(entry.Origin, Mathf.Min(entry.Radius, WideSearch))))
                            if (TryPoint(entry, candidate, out position)) { found = true; break; }
                        if (!found && !MustAppear(character))
                        {
                            logger?.LogInfo("No clear space for a " + scope.Description + "; " + character.name + " was removed.");
                            Remove(entry); continue;
                        }
                    }
                    bool crowded = !found && (scope.Summon || last);
                    if (crowded)
                    {
                        position = BestSpot(entry, search);
                        found = true;
                        logger?.LogInfo("No clear space for a " + scope.Description + "; " + character.name + " appears at the best place nearby.");
                    }
                    if (!found) { entry.Origin = OutOfFloor(entry.Origin); Reject(scope); return; }
                    PlaceNewCreature(character, entry.Origin, position); Physics.SyncTransforms();
                    if (crowded) CreaturePlacement.Settle(character);
                    Complete(entry, position);
                }
            }
            finally { scope.Resolving = false; }
        }

        // Nothing nearby is clear of the creature's body: the best place it can stand on its floor and side of any wall, even if
        // it overlaps something there, and failing that the very spot it started from.
        private static Vector3 BestSpot(Entry entry, float search)
        {
            foreach (var candidate in CreaturePlacement.Candidates(entry.Origin, search))
                if (TryPoint(entry, candidate, out var supported, clearance: false)) return supported;
            return entry.Origin;
        }

        private static bool TryPoint(Entry entry, Vector3 candidate, out Vector3 position, bool clearance = true)
        {
            position = candidate;
            bool original = candidate == entry.Origin;
            // Without its clearance a summon's spot keeps only its area, not a route the creature's body cannot use anyway.
            var check = clearance || entry.Scope.Area == null ? entry.Check : entry.Scope.Area;
            if (clearance && Utils.DistanceXZ(candidate, entry.Center) > entry.Radius + .01f || !check(ref candidate)) return false;
            if (!entry.Scope.OutdoorEncounter && Mathf.Abs(candidate.y + entry.Offset - entry.Origin.y) > 2f) return false;
            if (!SupportedPosition(entry.Character, candidate, entry.Offset, out position, original)) return false;
            if (entry.Scope.Parent && Vector3.Distance(position, entry.Center) > entry.Radius + .01f) return false;
            // Changing sides of a wall is not a nearby adjustment inside the same spawn area.
            var from = entry.Origin + Vector3.up; var to = position + Vector3.up;
            if (!entry.Scope.OutdoorEncounter && Physics.Linecast(from, to, out _, LayerMask.GetMask("Default", "static_solid", "piece", "piece_nonsolid"), QueryTriggerInteraction.Ignore)) return false;
            // Other pending bodies have no contacts yet. Releasing one restores its real collisions before the
            // next entry is checked, so a group can emerge in order without mutually blocking invisible members.
            return !clearance || CreaturePlacement.Clear(entry.Character, position, out _);
        }

        private static bool SupportedPosition(Character character, Vector3 candidate, float offset, out Vector3 position, bool allowOriginalAir = false)
        {
            position = candidate + Vector3.up * offset;
            if (character.IsFlying())
            {
                // The game drops a nest's flier onto the floor; one whose body reaches into that floor stands on it.
                if (CreaturePlacement.Floor(character, position, position.y, 0f, out var stand) && stand.y > position.y) position = stand;
                return true;
            }
            if (character.m_canSwim && Floating.GetLiquidLevel(position, 1f, LiquidType.Water) >= position.y)
            {
                // EpicLoot sets a swimming bounty target on the bed of shallow water. It rests on a sloping bed instead of
                // cutting into it; a swimmer placed higher in the water stays where it is.
                if (CreaturePlacement.Floor(character, candidate, candidate.y, offset, out var bed) && bed.y > position.y) position = bed;
                return true;
            }
            if (CreaturePlacement.Floor(character, candidate, candidate.y, offset, out position)) return true;
            // Some native producers deliberately start a creature above its floor. Preserve that fall at the
            // original point, but never move a rejected ground spawn over an edge or snap it to another storey.
            if (!allowOriginalAir) return false;
            position = candidate + Vector3.up * offset;
            foreach (var support in Physics.RaycastAll(position + Vector3.up * .1f, Vector3.down, 100f,
                LayerMask.GetMask("terrain", "Default", "static_solid", "piece", "piece_nonsolid", "vehicle"), QueryTriggerInteraction.Ignore).OrderBy(hit => hit.distance))
            {
                if (!CreaturePlacement.CanSupport(character, support.collider) || support.normal.y < .25f) continue;
                return support.distance > 2f;
            }
            return false;
        }

        private static void Complete(Entry entry, Vector3 position)
        {
            entry.Done = entry.Accepted = true; Pending.Remove(entry.Character);
            var view = GameFields.CharacterView(entry.Character);
            if (view.IsOwner()) view.GetZDO().Set(PendingKey, false);
            if (view.IsOwner()) ReleaseArea(entry.Character, position);
            FreshCreatureSpawns.Finish(entry.Character);
        }

        private static void PlaceNewCreature(Character character, Vector3 original, Vector3 position)
        {
            CreaturePlacement.Move(character, position);
            var ai = character.GetBaseAI(); var data = GameFields.CharacterView(character).GetZDO();
            if (!ai) return;
            SpawnPoint(ai) = position; data.Set(ZDOVars.s_spawnPoint, position);
            if (data.GetBool(ZDOVars.s_patrol) && (data.GetVec3(ZDOVars.s_patrolPoint, original) - original).sqrMagnitude < .01f)
                SetPatrolPoint(ai, position);
        }

        // The saved area effect is released once, by whichever game finishes the placement, and then cleared.
        private static void ReleaseArea(Character character, Vector3 position)
        {
            var data = GameFields.CharacterView(character).GetZDO();
            int saved = data.GetInt(AreaKey);
            var prefab = saved == 0 ? null : ZNetScene.instance.GetPrefab(saved);
            if (prefab)
            {
                var area = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity).GetComponent<Aoe>();
                if (area) area.Setup(character, Vector3.zero, -1f, null, null, null);
            }
            data.Set(AreaKey, 0);
        }

        private static void Reject(Scope scope)
        {
            if (scope.Failed) return;
            scope.NextAttempt = Time.time + 2f;
            // A summon whose factory step failed after creating it still appears once its setup finishes.
            if (scope.Summon) return;
            if (!scope.Reported)
            {
                scope.Reported = true;
                logger?.LogWarning("No clear position in the permitted area for " + scope.Description +
                    (scope.Discard ? "; this spawn was cancelled." : "; the selected creature waits for one to clear."));
            }
            // Fixed encounters, bounties and births keep the exact identity and rolled data already recorded by
            // their producer until their wait ends.
            if (!scope.Discard) return;
            scope.Failed = true;
            foreach (var entry in scope.Entries.Where(entry => !entry.Done).ToArray())
            {
                var character = entry.Character; var view = character ? GameFields.CharacterView(character) : null;
                if (!view || !view.IsValid() || !view.IsOwner()) continue;
                Remove(entry);
            }
        }

        private static void Remove(Entry entry)
        {
            entry.Done = true; Pending.Remove(entry.Character);
            FreshCreatureSpawns.Cancel(entry.Character); ZNetScene.instance.Destroy(entry.Character.gameObject);
        }

        internal static void Forget(Character character)
        {
            Fallbacks.Remove(character);
            Pending.Remove(character);
        }

        internal static void ClearWorld() { Pending.Clear(); current = null; }

        private static bool KeepPreviousSummons(Tameable __instance)
        {
            var character = __instance.GetComponent<Character>();
            return !character || !Marked(character);
        }
        internal static bool Marked(Character character) => PendingPlacement(GameFields.CharacterView(character)?.GetZDO());
        internal static bool PendingPlacement(ZDO data) => data?.GetBool(PendingKey) == true;
        private static bool AfterPlacementOnly(Character __instance) => !Marked(__instance);
        private static List<Character> PlacedCharacters() => Character.GetAllCharacters().Where(character => !Marked(character)).ToList();
        private static IEnumerable<CodeInstruction> CountPlacedSummons(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.GetAllCharacters))),
                AccessTools.Method(typeof(CreatureSpawnPlacement), nameof(PlacedCharacters)), 1, "The native summon-limit census changed.");

        internal static void AfterRelease(Character character)
        {
            var view = GameFields.CharacterView(character); var tame = character.GetComponent<Tameable>();
            if (!view || !view.IsValid()) return;
            var ai = character.GetBaseAI();
            if (ai) SpawnPoint(ai) = view.GetZDO().GetVec3(ZDOVars.s_spawnPoint, character.transform.position);
            if (!tame || !view.IsOwner()) return;
            int limit = view.GetZDO().GetInt(ZDOVars.s_maxInstances);
            if (limit > 0) EnforceSummonLimit(tame, limit);
        }
    }
}
