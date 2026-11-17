using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Logging;
using EpicLoot.Adventure;
using EpicLoot.Adventure.Feature;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // EpicLoot places a new bounty by snapping a random point in the biome's distance band to the nearest suitable cell of
    // its world biome index. A lone patch beside a wide stretch of sea or other biomes collects every point that lands in
    // that stretch, and it keeps a new bounty 200 m only from its own last spot in that biome, separately on each player's
    // game, so different players' bounties piled onto the same few spots. Choose
    // evenly among the band's suitable cells instead, at least Spacing from every open bounty and treasure chest in the
    // world, which the server reports. Bounties already placed keep their spots, and treasure maps keep EpicLoot's search.
    internal static class BountyPlacement
    {
        internal static float Spacing => Plugin.BountySpacing.Value;
        private const float ReplySeconds = 10f;
        private const float IndexSeconds = 30f;
        private const int ChecksPerFrame = 64;
        private const int CellsPerFrame = 512;
        private const string RequestRpc = Plugin.Id + ".bounty-spots";
        private const string ReplyRpc = Plugin.Id + ".bounty-spots-reply";
        private static readonly int SpawnerPrefab = "EL_SpawnController".GetStableHashCode();
        private static readonly int ChestPrefab = "loot_chest_stone".GetStableHashCode();
        private static readonly int ChestBiomeKey = "TreasureMapChest.Biome".GetStableHashCode();
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> Objects =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");
        private static readonly Dictionary<int, List<Vector2>> replies = new Dictionary<int, List<Vector2>>();
        private static int lastRequest;
        private static HashSet<int> creatures;
        private static ManualLogSource log;

        private delegate bool CandidateCheck(Heightmap.Biome biome, Vector2 candidate, float minRadius, float maxRadius, bool requireBand,
            bool requireZoneAgreement, ref int candidatesTried, out Vector3 point);
        private delegate void BandLookup(Heightmap.Biome biome, out float min, out float max);
        private static Action ensureBuilt;
        private static Func<object> indexState;
        private static Func<float> cellSize;
        private static Func<IDictionary> buckets;
        private static AccessTools.FieldRef<object, float[]> radii, eastings, northings;
        private static CandidateCheck check;
        private static BandLookup band;
        private static Func<Heightmap.Biome, AdventureSaveData, Action<bool, Vector3>, IEnumerator> epicLootSearch;
        private static Action<BountyInfo, Vector3, Vector3> spawn;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource logger)
        {
            log = logger;
            var assembly = typeof(BountyInfo).Assembly;
            var index = assembly.GetType("EpicLoot.Adventure.WorldBiomeIndex", true);
            var cache = assembly.GetType("EpicLoot.Adventure.Feature.BountyLocationEarlyCache", true);
            var bucket = AccessTools.Inner(index, "BiomeBucket");
            var state = AccessTools.PropertyGetter(index, "State");
            var size = AccessTools.PropertyGetter(index, "CellSize");
            var all = AccessTools.Field(index, "Buckets");
            if (state == null || size == null || all == null || bucket == null)
                throw new InvalidOperationException("EpicLoot's world biome index changed.");
            indexState = () => state.Invoke(null, null);
            cellSize = () => (float)size.Invoke(null, null);
            buckets = () => (IDictionary)all.GetValue(null);
            radii = AccessTools.FieldRefAccess<float[]>(bucket, "Radius");
            eastings = AccessTools.FieldRefAccess<float[]>(bucket, "X");
            northings = AccessTools.FieldRefAccess<float[]>(bucket, "Z");
            ensureBuilt = AccessTools.MethodDelegate<Action>(AccessTools.Method(index, "EnsureBuilt"));
            check = AccessTools.MethodDelegate<CandidateCheck>(AccessTools.Method(index, "TryCandidate"));
            band = AccessTools.MethodDelegate<BandLookup>(AccessTools.Method(cache, "GetRadiusBand"));
            epicLootSearch = AccessTools.MethodDelegate<Func<Heightmap.Biome, AdventureSaveData, Action<bool, Vector3>, IEnumerator>>(
                AccessTools.Method(cache, "TryGetBiomePoint"));
            spawn = AccessTools.MethodDelegate<Action<BountyInfo, Vector3, Vector3>>(
                AccessTools.Method(typeof(BountiesAdventureFeature), "SpawnBountyInitilizer"));
            harmony.Patch(AccessTools.Method(typeof(BountiesAdventureFeature), nameof(BountiesAdventureFeature.AcceptBounty)),
                prefix: new HarmonyMethod(typeof(BountyPlacement), nameof(Accept)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"), postfix: new HarmonyMethod(typeof(BountyPlacement), nameof(Register)));
        }

        private static bool Accept(Player player, BountyInfo bounty, Action<bool, Vector3> callback, ref IEnumerator __result)
        {
            __result = Place(player, bounty, callback);
            return false;
        }

        // EpicLoot's AcceptBounty with the spot chosen here and without its locating message. EpicLoot's own search still widens
        // an empty band or explains a failure.
        private static IEnumerator Place(Player player, BountyInfo bounty, Action<bool, Vector3> callback)
        {
            var saveData = player.GetAdventureSaveData();
            var spots = new List<Vector2>();
            yield return CollectSpots(saveData, spots);
            bool found = false;
            var point = Vector3.zero;
            yield return Pick(bounty.Biome, spots, (success, chosen) => { found = success; point = chosen; });
            if (!found)
            {
                log.LogWarning($"No suitable {bounty.Biome} spot in its band; EpicLoot places this bounty without keeping it clear of others.");
                yield return epicLootSearch(bounty.Biome, saveData, (success, chosen) => { found = success; point = chosen; });
            }
            if (found)
            {
                if (!saveData.AcceptedBounty(bounty, point, Vector3.zero))
                {
                    log.LogWarning($"Save data refused bounty {bounty.ID} ({bounty.Biome}, state={bounty.State}); nothing was spawned.");
                    callback?.Invoke(false, point);
                    yield break;
                }
                saveData.NumberOfTreasureMapsOrBountiesStarted++;
                player.SaveAdventureSaveData();
                spawn(bounty, point, Vector3.zero);
            }
            callback?.Invoke(found, point);
        }

        // This player's own open bounties and unfound treasure, which a slow reply cannot hide, then everyone's from the server.
        private static IEnumerator CollectSpots(AdventureSaveData saveData, List<Vector2> spots)
        {
            foreach (var known in saveData.Bounties)
                if (known.State == BountyState.InProgress) spots.Add(Flat((Vector3)known.Position + (Vector3)known.MinimapCircleOffset));
            foreach (var map in saveData.TreasureMaps)
                if (map.State == TreasureMapState.Purchased) spots.Add(Flat((Vector3)map.Position + (Vector3)map.MinimapCircleOffset));
            if (ZNet.instance.IsServer())
            {
                spots.AddRange(WorldSpots());
                yield break;
            }
            var server = ZNet.instance.GetServerPeer();
            if (server == null)
            {
                log.LogWarning("Not connected to a server; this bounty keeps clear of the player's own bounties and treasure only.");
                yield break;
            }
            int id = ++lastRequest;
            replies[id] = null;
            server.m_rpc.Invoke(RequestRpc, id);
            float deadline = Time.realtimeSinceStartup + ReplySeconds;
            while (replies[id] == null && Time.realtimeSinceStartup < deadline) yield return null;
            var reply = replies[id];
            replies.Remove(id);
            if (reply == null)
                log.LogWarning($"The server did not report open bounties and treasure within {ReplySeconds:0} seconds; this bounty keeps clear of the player's own only.");
            else spots.AddRange(reply);
        }

        // Evenly among the band's suitable cells at least Spacing from every spot, or failing that the one farthest from them.
        // Reports no spot only when the band has no suitable cell at all.
        private static IEnumerator Pick(Heightmap.Biome biome, List<Vector2> spots, Action<bool, Vector3> onComplete)
        {
            ensureBuilt();
            float deadline = Time.unscaledTime + IndexSeconds;
            while (indexState().ToString() == "Building" && Time.unscaledTime < deadline) yield return null;
            var all = indexState().ToString() == "Ready" ? buckets() : null;
            var cells = all != null && all.Contains(biome) ? all[biome] : null;
            if (cells == null)
            {
                onComplete(false, Vector3.zero);
                yield break;
            }
            band(biome, out float min, out float max);
            float[] radius = radii(cells), x = eastings(cells), z = northings(cells);
            int first = LowerBound(radius, min), count = UpperBound(radius, max) - first;
            var clearance = new float[Math.Max(count, 0)];
            var clear = new List<int>();
            for (int i = 0; i < count; i++)
            {
                if (i > 0 && i % CellsPerFrame == 0) yield return null;
                clearance[i] = Clearance(new Vector2(x[first + i], z[first + i]), spots);
                if (clearance[i] >= Spacing) clear.Add(i);
            }
            // Trying the clear cells in random order and keeping the first suitable one gives each of them the same chance.
            for (int i = clear.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (clear[i], clear[j]) = (clear[j], clear[i]);
            }
            int checks = 0, mixed = -1;
            foreach (int i in clear)
            {
                if (++checks % ChecksPerFrame == 0) yield return null;
                var cell = new Vector2(x[first + i], z[first + i]);
                if (Suitable(biome, cell, min, max, true, out var center))
                {
                    var point = Refine(biome, cell, center, min, max, true, spots);
                    log.LogInfo($"Placed a {biome} bounty at ({point.x:0}, {point.z:0}), {Flat(point).magnitude:0} m from the world center and " +
                        $"{Describe(Clearance(Flat(point), spots))} from the nearest of {spots.Count} open bounty and treasure spots; {clear.Count} of {count} band cells were clear.");
                    onComplete(true, point);
                    yield break;
                }
                // Like EpicLoot, accept a cell whose zone mixes biomes when no clear cell has a zone of its own.
                if (mixed < 0 && Suitable(biome, cell, min, max, false, out _)) mixed = i;
            }
            if (mixed >= 0)
            {
                var cell = new Vector2(x[first + mixed], z[first + mixed]);
                Suitable(biome, cell, min, max, false, out var center);
                var point = Refine(biome, cell, center, min, max, false, spots);
                log.LogInfo($"Placed a {biome} bounty at ({point.x:0}, {point.z:0}) in a mixed-biome zone, {Describe(Clearance(Flat(point), spots))} from the nearest open spot.");
                onComplete(true, point);
                yield break;
            }
            var farthest = Enumerable.Range(0, count).ToArray();
            Array.Sort(clearance.Select(value => -value).ToArray(), farthest);
            foreach (int i in farthest)
            {
                if (++checks % ChecksPerFrame == 0) yield return null;
                if (!Suitable(biome, new Vector2(x[first + i], z[first + i]), min, max, false, out var point)) continue;
                log.LogWarning($"No suitable {biome} cell in the {min:0}-{max:0} m band is {Spacing:0} m from all {spots.Count} open bounty and treasure spots; " +
                    $"placed this bounty at ({point.x:0}, {point.z:0}), the farthest, {clearance[i]:0} m from the nearest.");
                onComplete(true, point);
                yield break;
            }
            onComplete(false, Vector3.zero);
        }

        // Like EpicLoot, move up to half a cell away from the cell's center, staying clear of the open spots.
        private static Vector3 Refine(Heightmap.Biome biome, Vector2 cell, Vector3 center, float min, float max, bool ownZone, List<Vector2> spots)
        {
            float reach = cellSize() * 0.5f;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                var candidate = cell + UnityEngine.Random.insideUnitCircle * reach;
                if (Clearance(candidate, spots) >= Spacing && Suitable(biome, candidate, min, max, ownZone, out var point)) return point;
            }
            return center;
        }

        // EpicLoot's own rules: in the band and the biome, not deep underwater or on lava, optionally with the whole zone in the biome.
        private static bool Suitable(Heightmap.Biome biome, Vector2 candidate, float min, float max, bool ownZone, out Vector3 point)
        {
            int tried = 0;
            return check(biome, candidate, min, max, true, ownZone, ref tried, out point);
        }

        private static float Clearance(Vector2 point, List<Vector2> spots)
        {
            float nearest = float.PositiveInfinity;
            foreach (var spot in spots) nearest = Math.Min(nearest, (spot - point).sqrMagnitude);
            return Mathf.Sqrt(nearest);
        }

        private static string Describe(float clearance) => float.IsPositiveInfinity(clearance) ? "any distance" : $"{clearance:0} m";

        private static Vector2 Flat(Vector3 position) => new Vector2(position.x, position.z);

        private static int LowerBound(float[] sorted, float value)
        {
            int low = 0, high = sorted.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (sorted[middle] < value) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private static int UpperBound(float[] sorted, float value)
        {
            int low = 0, high = sorted.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (sorted[middle] <= value) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private static void Register(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer()) peer.m_rpc.Register<int>(RequestRpc, (rpc, id) => rpc.Invoke(ReplyRpc, id, Pack(WorldSpots())));
            else peer.m_rpc.Register<int, ZPackage>(ReplyRpc, Receive);
        }

        private static void Receive(ZRpc rpc, int id, ZPackage package)
        {
            if (!replies.TryGetValue(id, out var pending) || pending != null) return;
            int count = package.ReadInt();
            var spots = new List<Vector2>(count);
            for (int i = 0; i < count; i++) spots.Add(new Vector2(package.ReadSingle(), package.ReadSingle()));
            replies[id] = spots;
        }

        private static ZPackage Pack(List<Vector2> spots)
        {
            var package = new ZPackage();
            package.Write(spots.Count);
            foreach (var spot in spots)
            {
                package.Write(spot.x);
                package.Write(spot.y);
            }
            return package;
        }

        // Spawners still waiting for a player, living bounty creatures with their bounty's own spot, and unopened treasure chests,
        // which EpicLoot removes once emptied.
        private static List<Vector2> WorldSpots()
        {
            var watch = Stopwatch.StartNew();
            creatures ??= new HashSet<int>(ZNetScene.instance.m_prefabs.Where(prefab => prefab.GetComponent<Character>()).Select(prefab => prefab.name.GetStableHashCode()));
            var spots = new List<Vector2>();
            var bounties = new HashSet<string>();
            foreach (var zdo in Objects(ZDOMan.instance).Values)
            {
                int prefab = zdo.GetPrefab();
                if (prefab == SpawnerPrefab || prefab == ChestPrefab && zdo.GetString(ChestBiomeKey).Length > 0) spots.Add(Flat(zdo.GetPosition()));
                else if (creatures.Contains(prefab))
                {
                    string id = zdo.GetString(BountyCreature.IdKey);
                    if (id.Length == 0) continue;
                    spots.Add(Flat(zdo.GetPosition()));
                    if (bounties.Add(id) && TryReadSpot(zdo, out var spot)) spots.Add(spot);
                }
            }
            log.LogInfo($"Reported {spots.Count} open bounty and treasure spots in {watch.ElapsedMilliseconds} ms.");
            return spots;
        }

        private static bool TryReadSpot(ZDO zdo, out Vector2 spot)
        {
            spot = Vector2.zero;
            if (!BountyCreature.TryRead(zdo, out var bounty)) return false;
            spot = Flat((Vector3)bounty.Position + (Vector3)bounty.MinimapCircleOffset);
            return true;
        }
    }
}
