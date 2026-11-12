using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class CreaturePrefabs
    {
        private static readonly Dictionary<int, bool> Kinds = new Dictionary<int, bool>();

        internal static bool IsCreature(GameObject prefab) => IsCreature(prefab, new HashSet<GameObject>());

        private static bool IsCreature(GameObject prefab, HashSet<GameObject> visited)
        {
            if (prefab == null || !visited.Add(prefab) || prefab.GetComponent<Player>() != null || prefab.GetComponent<Fish>() != null) return false;
            if (prefab.GetComponent<BaseAI>() != null) return true;
            var spawner = prefab.GetComponent<CreatureSpawner>();
            if (spawner != null && IsCreature(spawner.m_creaturePrefab, visited)) return true;
            var area = prefab.GetComponent<SpawnArea>();
            return area != null && area.m_prefabs.Any(entry => IsCreature(entry.m_prefab, visited));
        }

        internal static bool IsCreature(ZDO data)
        {
            if (ZNetScene.instance == null) return false;
            int prefab = data.GetPrefab();
            if (!Kinds.TryGetValue(prefab, out bool creature))
            {
                var definition = ZNetScene.instance.GetPrefab(prefab);
                if (definition == null) return false;
                Kinds[prefab] = creature = IsCreature(definition);
            }
            return creature;
        }
    }

    [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawnList")]
    internal static class OrdinaryCreatureSpawns
    {
        // Fuling berserkers take a fifth of the Vile's night spawns: its 25% every 4000 s, split into 20% and 5%.
        private const float VileChance = 20f, BerserkerChance = 5f;
        internal const float BerserkerInterval = 4000f;

        internal static bool Berserker(SpawnSystem.SpawnData entry) => entry.m_prefab && entry.m_name == "GoblinBrute" && entry.m_prefab.name == "GoblinBrute";

        // Clone before PheromoneSpawns, which then clones and tracks the potion's final entries itself.
        [HarmonyPriority(Priority.First)]
        private static void Prefix(ref List<SpawnSystem.SpawnData> spawners, bool eventSpawners)
        {
            if (eventSpawners) return;
            List<SpawnSystem.SpawnData> adjusted = null;
            for (int index = 0; index < spawners.Count; index++)
            {
                var entry = spawners[index];
                float minimum = entry.m_spawnRadiusMin > 0f ? entry.m_spawnRadiusMin : 40f;
                float maximum = entry.m_spawnRadiusMax > 0f ? entry.m_spawnRadiusMax : 80f;
                bool distance = (minimum == 40f || minimum == 50f) && maximum == 80f && CreaturePrefabs.IsCreature(entry.m_prefab);
                bool patrol = entry.m_prefab &&
                    (entry.m_name == "Jotun Witch Patrol" && entry.m_prefab.name == "JotunWitch" ||
                     entry.m_name == "Jotun Melee Patrol" && entry.m_prefab.name == "JotunWarrior");
                bool vile = entry.m_prefab && entry.m_name == "Unbjorn" && entry.m_prefab.name == "Unbjorn";
                bool berserker = Berserker(entry);
                if (!distance && !patrol && !vile && !berserker) continue;
                if (adjusted == null) adjusted = new List<SpawnSystem.SpawnData>(spawners);
                var copy = entry.Clone();
                if (distance)
                {
                    copy.m_spawnRadiusMin = Plugin.CreatureSpawnMinimum.Value;
                    copy.m_spawnRadiusMax = Plugin.CreatureSpawnMaximum.Value;
                }
                if (patrol) copy.m_spawnChance = 15f;
                if (vile) copy.m_spawnChance = VileChance;
                if (berserker)
                {
                    copy.m_spawnAtNight = true;
                    copy.m_spawnInterval = BerserkerInterval;
                    copy.m_spawnChance = BerserkerChance;
                }
                adjusted[index] = copy;
            }
            if (adjusted != null) spawners = adjusted;
        }
    }

    internal static class FullLoading
    {
        internal static SimulationDistance Required => SimulationPresets.Distance(4);
        internal static SimulationDistance DesiredDistance => SimulationPresets.AfterProjectiles(ZNet.instance != null && ZNet.instance.IsDedicated() ? Required :
            SimulationPresets.Distance(GraphicsSettingsManager.Instance == null ? 4 : GraphicsSettingsManager.Instance.CurrentPlayerSettingsRaw.m_simulationDistance));

        [HarmonyPatch(typeof(ZNet), "GetDesiredSimulationDistance")]
        private static class Desired
        {
            private static bool Prefix(ref SimulationDistance __result) { __result = DesiredDistance; return false; }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.ApplySimulationDistance))]
        private static class Applied
        {
            private static void Prefix(ZNet __instance, ref SimulationDistance simulationDistance) =>
                simulationDistance = __instance.IsDedicated() ? Required : SimulationPresets.Normalize(simulationDistance);
            private static void Postfix() => CreatureOwnership.RefreshPublication();
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class Started
        {
            private static void Postfix(ZNet __instance) => __instance.SimulationDistanceServerHandshake();
        }

        [HarmonyPatch(typeof(ZNet), "RPC_RequestValidSimulationDistance")]
        private static class Requested
        {
            private static void Prefix(ZNet __instance, ref ZPackage pkg)
            {
                if (!__instance.IsServer()) return;
                var requested = SimulationPresets.Normalize(SimulationDistance.Deserialize(ref pkg));
                pkg = new ZPackage();
                requested.Serialize(ref pkg);
                pkg.SetPos(0);
            }
        }
    }

    internal static class CreatureOwnership
    {
        private static readonly int LoadedSectorsKey = "epicloot_progression_loaded_sectors".GetStableHashCode();
        private static readonly List<ZDO> NearObjects = new List<ZDO>();
        private static float nextPublication;

        internal static void RefreshPublication() => nextPublication = 0f;

        internal static bool InNearArea(Vector2s center, Vector2s sector, SimulationDistance distance) =>
            Math.Abs(center.x - sector.x) <= distance.NearSimulationDistance && Math.Abs(center.y - sector.y) <= distance.NearSimulationDistance &&
            (distance.IsClassic || ZoneSystem.instance.ZonesWithinRadius(center, sector, distance.NearSimulationDistance));

        // The server knows negotiated distances, but not which client terrain has finished loading. Publish only
        // loaded near sectors through the player's existing ZDO. Delimiters keep coordinates unambiguous; the short
        // string changes on sector loading/unloading, not on every player movement or ownership check.
        [HarmonyPatch(typeof(Player), "Update")]
        private static class LoadedSectors
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || Time.time < nextPublication || ZoneSystem.instance == null) return;
                nextPublication = Time.time + 1f;
                var view = GameFields.CharacterView(__instance);
                if (view == null || !view.IsValid() || !view.IsOwner()) return;
                var center = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
                var distance = ZNet.instance.GetSyncedSimulationDistance();
                var loaded = new StringBuilder("|");
                int radius = distance.NearSimulationDistance;
                for (int y = center.y - radius; y <= center.y + radius; y++)
                    for (int x = center.x - radius; x <= center.x + radius; x++)
                    {
                        var sector = new Vector2s(x, y);
                        if (InNearArea(center, sector, distance) && ZoneSystem.instance.IsZoneLoaded(sector)) loaded.Append(x).Append(',').Append(y).Append('|');
                    }
                string value = loaded.ToString();
                var data = view.GetZDO();
                if (data.GetString(LoadedSectorsKey) != value) data.Set(LoadedSectorsKey, value);
            }
        }

        internal static bool CanOwn(ZDO creature, long owner)
        {
            if (owner == 0 || ZNet.instance == null || ZoneSystem.instance == null) return false;
            Vector3 reference;
            SimulationDistance distance;
            ZDO player = null;
            bool local = owner == ZDOMan.GetSessionID();
            if (local)
            {
                if (ZNet.instance.IsDedicated() || Player.m_localPlayer == null) return false;
                reference = ZNet.instance.GetReferencePosition();
                distance = ZNet.instance.GetSyncedSimulationDistance();
            }
            else
            {
                var peer = ZNet.instance.GetPeer(owner);
                if (peer == null || !peer.IsReady() || !peer.m_rpc.IsConnected() || peer.m_server || peer.m_characterID.IsNone()) return false;
                player = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (player == null) return false;
                reference = peer.GetRefPos();
                distance = peer.m_simulationDistance;
            }
            var position = creature.GetPosition();
            var delta = position - reference;
            float radius = SimulationPresets.Radius(distance);
            if (!(delta.x * delta.x + delta.z * delta.z <= radius * radius)) return false;
            var sector = ZoneSystem.GetZone(position);
            if (!InNearArea(ZoneSystem.GetZone(reference), sector, distance)) return false;
            return local ? ZoneSystem.instance.IsZoneLoaded(sector) : player.GetString(LoadedSectorsKey).Contains("|" + sector.x + "," + sector.y + "|");
        }

        private static bool UseNativeOwnership(ZDO data, long candidate)
        {
            if (!data.Persistent) return false;
            if (!CreaturePrefabs.IsCreature(data)) return true;
            long owner = data.GetOwner();
            if (!CanOwn(data, owner)) Reassign(data, owner, candidate);
            return false;
        }

        private static void Reassign(ZDO creature, long owner, long candidate)
        {
            if (CanOwn(creature, candidate)) creature.SetOwner(candidate);
            else if (owner != 0) creature.SetOwner(0);
        }

        // A creature the server owns inside its own active area stays there, as the game leaves anything with an owner that
        // has it in range. A dedicated server's area lies outside the world, so this holds nothing a player can meet.
        internal static bool ServerKeeps(ZDO creature) => creature.GetOwner() == ZDOMan.GetSessionID()
            && ZNetScene.InActiveArea(creature.GetPosition(), ZNet.instance.GetReferencePosition());

        // A mod that replaces the game's ownership pass calls this for the same positions. A creature with an owner in range
        // stays where it is, so which of several players in range runs it remains that mod's choice.
        internal static void ApplyRange(ZDOMan manager, Vector3 refPosition, long uid)
        {
            var native = ZNet.instance.GetSyncedSimulationDistance();
            manager.FindSectorObjects(ZoneSystem.GetZone(refPosition), new SimulationDistance(native.NearSimulationDistance, 0, native.IsClassic), NearObjects);
            foreach (var data in NearObjects)
            {
                if (!data.Persistent || !CreaturePrefabs.IsCreature(data) || ServerKeeps(data)) continue;
                long owner = data.GetOwner();
                if (!CanOwn(data, owner)) Reassign(data, owner, uid);
            }
            NearObjects.Clear();
        }

        private static bool NativeActiveArea(Vector3 point, Vector2s center, long owner)
        {
            var peer = owner == ZDOMan.GetSessionID() ? null : ZNet.instance.GetPeer(owner);
            if (owner != ZDOMan.GetSessionID() && peer == null) return false;
            var distance = peer == null ? ZNet.instance.GetSyncedSimulationDistance() : peer.m_simulationDistance;
            var delta = point - ZoneSystem.GetZonePos(center); delta.y = 0f;
            float zoneSize = ZoneSystem.instance.m_zoneSize;
            float half = (distance.NearSimulationDistance == 1 ? 1f : 1.5f) * zoneSize;
            if (Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.z)) > half) return false;
            return distance.NearSimulationDistance != 2 || distance.IsClassic || delta.sqrMagnitude < zoneSize * zoneSize * 1.75f * 1.75f;
        }

        [HarmonyPatch(typeof(ZDOMan), "IsInPeerActiveArea")]
        private static class PeerActiveArea
        {
            private static bool Prefix(Vector3 point, long uid, ref bool __result)
            {
                var peer = uid == ZDOMan.GetSessionID() ? null : ZNet.instance.GetPeer(uid);
                __result = (uid == ZDOMan.GetSessionID() || peer != null) && NativeActiveArea(point,
                    ZoneSystem.GetZone(peer == null ? ZNet.instance.GetReferencePosition() : peer.GetRefPos()), uid);
                return false;
            }
        }

        // Only replace the per-object gate in the native ownership loop. Everything except AI creatures and their
        // spawners continues through Valheim's unchanged selection and retention path.
        [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
        private static class OwnershipSelection
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var persistent = AccessTools.PropertyGetter(typeof(ZDO), nameof(ZDO.Persistent));
                var matches = Enumerable.Range(0, code.Count).Where(index => code[index].Calls(persistent)).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("Valheim's ownership loop changed; expected one persistent-object gate.");
                int at = matches[0];
                var loadOwner = new CodeInstruction(OpCodes.Ldarg_2) { labels = code[at].labels, blocks = code[at].blocks };
                code[at] = loadOwner;
                code.Insert(at + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CreatureOwnership), nameof(UseNativeOwnership))));
                var active = AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.InActiveArea), new[] { typeof(Vector3), typeof(Vector2s) });
                int areas = 0;
                for (int index = code.Count - 1; index >= 0; index--)
                {
                    if (!code[index].Calls(active)) continue;
                    var previous = code[index];
                    code[index] = new CodeInstruction(OpCodes.Ldarg_2) { labels = previous.labels, blocks = previous.blocks };
                    code.Insert(index + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CreatureOwnership), nameof(NativeActiveArea))));
                    areas++;
                }
                if (areas != 2) throw new InvalidOperationException("Valheim's ownership area checks changed.");
                return code;
            }
        }
    }
}
