using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using EpicLoot;
using EpicLoot.Adventure;
using EpicLoot.src.Magic.MagicItemEffects.Helpers;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static partial class CreatureSpawnPlacement
    {
        private static readonly AccessTools.FieldRef<SpawnAbility, Character> AbilityOwner = AccessTools.FieldRefAccess<SpawnAbility, Character>("m_owner");
        private sealed class AbilityPlacement { internal Vector3 Target; }
        private static readonly ConditionalWeakTable<SpawnAbility, AbilityPlacement> Abilities = new();
        private static MethodInfo bountyCandidate;
        private static FieldInfo bountyPosition, bountyUsable, bountyTier, bountyReady, bountySelectedTier;
        private static Func<Heightmap.Biome, bool> openWater;
        private delegate bool NemesisGroundCheck(Vector3 point, Heightmap.Biome biome, out Vector3 ground);
        private static NemesisGroundCheck nemesisGround;
        private static string nemesisSpawnKey;

        // Set while a dying creature's loot is created, including the blobs an oozer releases.
        [ThreadStatic] private static int droppingLoot;

        private static void InitializeAdditionalRoutes(IntegrationPatches patches)
        {
            var type = typeof(CreatureSpawnPlacement);
            patches.Patch(AccessTools.Method(typeof(CharacterDrop), nameof(CharacterDrop.DropItems)),
                prefix: new HarmonyMethod(type, nameof(BeforeLoot)), finalizer: new HarmonyMethod(type, nameof(AfterLoot)));
            if (StarLevelSystem.Installed)
            {
                var loot = StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.Loot.LootPerformanceChanges", true);
                var immediate = AccessTools.Method(loot, "DropItemsImmediate"); var spread = AccessTools.Method(loot, "DropItemsAsync");
                if (immediate == null || spread?.ReturnType != typeof(IEnumerator)) throw new InvalidOperationException("StarLevelSystem's loot drops changed.");
                patches.Patch(immediate, prefix: new HarmonyMethod(type, nameof(BeforeLoot)), finalizer: new HarmonyMethod(type, nameof(AfterLoot)));
                patches.Patch(spread, postfix: new HarmonyMethod(type, nameof(SpreadLoot)));
                var remote = StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.NemesisSystem.NemesisRemoteSpawnControl", true);
                nemesisGround = AccessTools.MethodDelegate<NemesisGroundCheck>(AccessTools.Method(remote, "IsSpawnLocationValid"));
                nemesisSpawnKey = (string)AccessTools.Field(StarLevelSystem.Assembly.GetType("StarLevelSystem.common.DataObjects", true), "SLS_NEMESIS_SPAWN").GetValue(null);
                patches.Patch(AccessTools.Method(remote, "SpawnNemesisSpawn"),
                    prefix: new HarmonyMethod(type, nameof(BeforeNemesis)) { priority = Priority.Last }, finalizer: new HarmonyMethod(type, nameof(AfterFactory)));
            }
            patches.Patch(AccessTools.Method(typeof(SpawnAbility), "Spawn"), postfix: new HarmonyMethod(type, nameof(AbilityRoutine)));
            patches.Patch(AccessTools.Method(typeof(SpawnAbility), "FindTarget"), postfix: new HarmonyMethod(type, nameof(AbilityTarget)));
            patches.Patch(AccessTools.Method(typeof(SpawnAbility), "SetupAoe"), prefix: new HarmonyMethod(type, nameof(DeferAbilityArea)));
            patches.Patch(AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(SpawnAbility), "Spawn")), transpiler: new HarmonyMethod(type, nameof(SummonGround)));
            var epic = typeof(EpicLoot.EpicLoot).Assembly;
            patches.Patch(AccessTools.Method(epic.GetType("EpicLoot.MagicItemEffects.Shards.SummonBatWhenActivatingAdrenaline", true), "SummonBats"),
                prefix: new HarmonyMethod(type, nameof(BeforeBats)), finalizer: new HarmonyMethod(type, nameof(AfterFactory)));
            var bounty = epic.GetType("EpicLoot.Adventure.AdventureSpawnController", true);
            bountyCandidate = AccessTools.Method(bounty, "EvaluateCandidate");
            var candidate = bountyCandidate.ReturnType;
            bountyPosition = AccessTools.Field(candidate, "Position"); bountyUsable = AccessTools.Field(candidate, "Usable"); bountyTier = AccessTools.Field(candidate, "Tier");
            bountyReady = AccessTools.Field(bounty, "zoneReadyCache"); bountySelectedTier = AccessTools.Field(bounty, "placedTier");
            var index = Array.Find(epic.GetTypes(), value => value.Name == "WorldBiomeIndex");
            openWater = AccessTools.MethodDelegate<Func<Heightmap.Biome, bool>>(AccessTools.Method(index, "IsOpenWater"));
            patches.Patch(AccessTools.Method(bounty, "SpawnBountyTargets"), prefix: new HarmonyMethod(type, nameof(BeforeBounty)), finalizer: new HarmonyMethod(type, nameof(AfterFactory)));
        }

        // Creatures in loot, such as the blobs an oozer releases, appear at once where they drop, as natively. Held for a body-clear
        // spot, each blob waited for the ones before it to hop away, two seconds per try.
        private static void BeforeLoot() => droppingLoot++;
        private static Exception AfterLoot(Exception __exception) { droppingLoot--; return __exception; }
        private static void SpreadLoot(ref IEnumerator __result) => __result = DropLoot(__result);

        private static IEnumerator DropLoot(IEnumerator spawning)
        {
            try
            {
                while (true)
                {
                    bool more;
                    droppingLoot++;
                    try { more = spawning.MoveNext(); }
                    finally { droppingLoot--; }
                    if (!more) yield break;
                    yield return spawning.Current;
                }
            }
            finally { (spawning as IDisposable)?.Dispose(); }
        }

        private static bool NearEncounterGround(Vector3 point) => Mathf.Abs(point.y - ZoneSystem.instance.GetGroundHeight(point)) <= 8f;
        private static bool NemesisPlacement(ZDO data) => nemesisSpawnKey != null && data.GetBool(nemesisSpawnKey) && NearEncounterGround(data.GetPosition());

        // SLS can create a whole ambush during a village Draugr spawner's setup. Its creatures must not inherit that
        // spawner's indoor floor and wall constraints: choose outdoor ground before any of them enter simulation.
        // Use SLS's own remote-miniboss ground rules, including biome, roofs, lava, sea level, bases and wards.
        private static Scope NemesisScope(Vector3 origin)
        {
            var biome = WorldGenerator.instance.GetBiome(origin.x, origin.z);
            PointCheck check = (ref Vector3 point) =>
            {
                if (!ZoneSystem.instance.IsZoneLoaded(ZoneSystem.GetZone(point)) ||
                    !nemesisGround(point, biome, out var ground) || Mathf.Abs(ground.y - origin.y) > 8f || NearPlayerBuild(ground)) return false;
                point = ground; return true;
            };
            // Search the nearby outdoor area immediately, including room for escorts, rather than waiting ten seconds
            // before widening the body-sized search. With no safe spot, keep the original creature pending, never force
            // it into a building or discard a selected escort. A saved pending encounter resumes the same rules.
            return new Scope { Center = origin, Radius = 30f, Check = check, OutdoorEncounter = true, Description = "Nemesis encounter" };
        }

        // Airborne encounters and dungeon interiors retain their existing placement; they must not be pulled to surface terrain.
        private static void BeforeNemesis(Vector3 __1, out Call __state) => __state = NearEncounterGround(__1) ? Enter(NemesisScope(__1)) : null;

        private static PointCheck SameArea(Vector3 origin, Component source = null)
        {
            var room = source ? source.GetComponentInParent<Room>() : null;
            var inverse = room ? room.transform.worldToLocalMatrix : Matrix4x4.identity;
            var bounds = room ? new Bounds(Vector3.zero, (Vector3)room.m_size) : default;
            var biome = WorldGenerator.instance.GetBiome(origin.x, origin.z);
            // A location can load after the creature inside it, so its area is read when a spot is judged.
            return (ref Vector3 point) => Location.GetLocation(point) == Location.GetLocation(origin) &&
                (!room || bounds.Contains(inverse.MultiplyPoint3x4(point))) &&
                WorldGenerator.instance.GetBiome(point.x, point.z) == biome;
        }

        private static void AbilityTarget(SpawnAbility __instance, Vector3 point, bool __result)
        {
            if (__result) Abilities.GetOrCreateValue(__instance).Target = point;
        }

        private static void AbilityRoutine(SpawnAbility __instance, ref IEnumerator __result) => __result = PlaceAbility(__instance, __result);

        private static IEnumerator PlaceAbility(SpawnAbility ability, IEnumerator routine)
        {
            var state = Abilities.GetOrCreateValue(ability); state.Target = ability.transform.position;
            bool summon = AbilityOwner(ability) is Player;
            try
            {
                while (true)
                {
                    // A coroutine can yield after selecting its target but before creating the creature. The scope
                    // exists only during its next native step, never across the yield or another creature's spawn.
                    var scope = new Scope { Ability = ability, Center = ability.transform.position, Radius = ability.m_spawnRadius,
                        Offset = ability.m_targetType == SpawnAbility.TargetType.RandomPathfindablePosition ? 0f : ability.m_spawnGroundOffset,
                        Summon = summon, Description = summon ? "player summon" : "summoning ability" };
                    var call = Enter(scope); bool more; Exception failure = null;
                    try { more = routine.MoveNext(); }
                    catch (Exception error) { failure = error; throw; }
                    finally
                    {
                        // Capture reads the target selected in the current step, including after a pre-spawn delay.
                        AfterFactory(failure, call);
                    }
                    if (!more) yield break;
                    yield return routine.Current;
                }
            }
            finally { (routine as IDisposable)?.Dispose(); }
        }

        private static void ConfigureAbility(Scope scope, Vector3 origin)
        {
            var ability = scope.Ability; var owner = AbilityOwner(ability);
            bool path = ability.m_targetType == SpawnAbility.TargetType.RandomPathfindablePosition;
            var center = path || !ability.m_spawnAtTarget ? ability.transform.position : Abilities.GetOrCreateValue(ability).Target;
            scope.Center = center;
            var originalArea = SameArea(center); float radius = scope.Radius, offset = scope.Offset;
            var profile = ability.m_targetWhenPathfindingType;
            // Under a low ceiling, such as a crypt passage, the native cast finds no walkable point and summons at the
            // caster. That creature takes the nearest clear space on the caster's side of any wall instead.
            bool atCaster = path && Utils.DistanceXZ(origin, center) < .01f;
            scope.Area = (ref Vector3 point) =>
            {
                point.y -= offset;
                return originalArea(ref point);
            };
            scope.Check = (ref Vector3 point) =>
            {
                point.y -= offset;
                if (!originalArea(ref point)) return false;
                if (!path || atCaster) return true;
                return Utils.DistanceXZ(point, center) >= radius / 2f && owner && Pathfinding.instance.GetPath(owner.transform.position, point,
                    new List<Vector3>(), profile, requireFullPath: true, cleanup: false, havePath: true);
            };
        }

        private static bool DeferAbilityArea(SpawnAbility __instance, Character owner)
        {
            if (!owner || !Pending.TryGetValue(owner, out var entry)) return true;
            entry.Aoe = __instance.m_aoePrefab;
            if (entry.Aoe) GameFields.CharacterView(owner).GetZDO().Set(AreaKey, entry.Aoe.name.GetStableHashCode());
            return false;
        }

        // The game's ground probe looks down from far above, so a root cast under a roof came up on the roof, and when the first
        // thing the probe met was a loose body the game put the root at height 0. A player's root stands on the first solid
        // surface below the point the staff hit, or at that point; one that hit a ceiling stands on the floor under it.
        private static IEnumerable<CodeInstruction> SummonGround(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetSolidHeight),
                new[] { typeof(Vector3), typeof(float).MakeByRefType(), typeof(int) })), AccessTools.Method(typeof(CreatureSpawnPlacement), nameof(SummonHeight)), 1,
                "The summoning ability's ground probe changed.");

        private static bool SummonHeight(ZoneSystem zone, Vector3 point, out float height, int margin)
        {
            bool found = zone.GetSolidHeight(point, out height, margin);
            if (current == null || !current.Summon) return found;
            int solids = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            var start = point + Vector3.up * .5f;
            // The game's height counts when it lies within 1 m above the point, so a ceiling is looked for as far. A probe that
            // starts on a floor meets the floor's top; only a solid surface facing down is a ceiling.
            var ceiling = Physics.RaycastAll(point + Vector3.down * .1f, Vector3.up, 1.2f, solids, QueryTriggerInteraction.Ignore)
                .Where(hit => hit.normal.y < -.5f && !hit.collider.attachedRigidbody).OrderBy(hit => hit.distance).Take(1).ToList();
            if (ceiling.Count > 0) start = ceiling[0].point + Vector3.down * .05f;
            else if (found && height <= point.y + 1f) return true;
            height = point.y;
            foreach (var hit in Physics.RaycastAll(start, Vector3.down, 10f, solids, QueryTriggerInteraction.Ignore).OrderBy(hit => hit.distance))
                if (!hit.collider.attachedRigidbody) { height = hit.point.y; break; }
            return true;
        }

        private static void BeforeBats(Player player, out Call __state)
        {
            var center = player.transform.position + Vector3.up;
            float radius = Mathf.Abs(EffectConfig.Get(MagicEffectType.SummonBatWhenActivatingAdrenaline, "SpawnRadius", 2f));
            var area = SameArea(center);
            PointCheck check = (ref Vector3 point) => area(ref point) && Mathf.Abs(point.y - center.y) < .01f &&
                Mathf.Abs(Utils.DistanceXZ(point, center) - radius) < .01f;
            // Keep the native ring, swarm limit and already-running lifetime. A blocked bat takes another clear point on that
            // ring, or else appears where the game put it; it is never recast or refreshed.
            __state = Enter(new Scope { Center = center, Radius = radius, Check = check, Area = area, Summon = true, Description = "shardstone bats" });
        }

        private static void BeforeBounty(MonoBehaviour __instance, BountyInfo bounty, out Call __state)
        {
            var origin = __instance.GetComponent<ZNetView>().GetZDO().GetVec3("spawnPoint", __instance.transform.position);
            bool water = openWater(bounty.Biome); float surface = ZoneSystem.instance.m_waterLevel;
            float buffer = Mathf.Max(0f, AdventureDataManager.Config.TreasureMap.MinimapAreaRadius);
            int evaluatedFrame = -1;
            object Evaluate(Vector3 point)
            {
                // Native search owns this cache only for one search stage. Pending creatures can outlive that
                // stage and its controller, so refresh readiness for each later placement pass.
                if (evaluatedFrame != Time.frameCount)
                { ((IDictionary)bountyReady.GetValue(__instance)).Clear(); evaluatedFrame = Time.frameCount; }
                return bountyCandidate.Invoke(__instance, new object[] { point, bounty.Biome, water, surface, buffer });
            }
            int originalTier = Convert.ToInt32(bountySelectedTier.GetValue(__instance));
            var sameArea = SameArea(origin);
            PointCheck check = (ref Vector3 point) =>
            {
                if (!sameArea(ref point)) return false;
                var result = Evaluate(point);
                if (!(bool)bountyUsable.GetValue(result) || Convert.ToInt32(bountyTier.GetValue(result)) > originalTier) return false;
                if (!water) point = (Vector3)bountyPosition.GetValue(result);
                return true;
            };
            __state = Enter(new Scope { Center = origin, Radius = Mathf.Clamp(MinimapController.AreaRadius * .19999999f, 0f, 4f),
                Check = check, Description = "bounty encounter" });
        }
    }
}
