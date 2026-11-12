using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Network Performance System runs its own ownership pass instead of the game's, picking the player an object looks
    // least stale to and moving creatures off a player whose upload or frame rate cannot carry them. That pass reads each
    // player's position, as the game's does, so it cannot know which of them has finished loading the ground a creature
    // stands on; this add-on does know, because every player publishes its loaded sectors. That pass also gives a creature
    // nobody owns only to a player in the 3x3 zones around it, and lets a player keep one as far as its game loads. This
    // add-on's rules therefore reach that pass instead of the game's: a creature goes only to a player whose game has that
    // zone, the creature range decides who gets and leaves one outside those zones, and what a player left behind in its far
    // area after a portal or a respawn is still released, which its pass leaves owned.
    internal static class NetworkPerformanceOwnership
    {
        internal static bool Arbitrating { get; private set; }

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!Chainloader.PluginInfos.ContainsKey(PluginIds.NetworkPerformance)) return;
            var arbiter = AccessTools.TypeByName("NetworkPerformanceSystem.Runtime.OwnershipArbiter");
            var pass = arbiter == null ? null : AccessTools.Method(arbiter, "RunPass", new[] { typeof(ZDOMan) });
            if (pass == null)
            {
                DisabledFeatures.Set("Creature range under Network Performance System", "its ownership pass moved");
                return;
            }
            harmony.Patch(pass, prefix: new HarmonyMethod(typeof(NetworkPerformanceOwnership), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(NetworkPerformanceOwnership), nameof(End)));
            harmony.Patch(AccessTools.Method(typeof(ZDO), nameof(ZDO.SetOwner)),
                prefix: new HarmonyMethod(typeof(NetworkPerformanceOwnership), nameof(KeepCreatureEligible)));
        }

        private static void Begin() => Arbitrating = true;

        // That pass is a static method, so it has no instance to take the object manager from.
        private static void End()
        {
            Arbitrating = false;
            var manager = ZDOMan.instance;
            if (manager == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            Apply(manager, ZNet.instance.GetReferencePosition(), ZDOMan.GetSessionID());
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer.IsReady()) Apply(manager, peer.GetRefPos(), peer.m_uid);
        }

        private static void Apply(ZDOMan manager, Vector3 refPosition, long uid)
        {
            CreatureOwnership.ApplyRange(manager, refPosition, uid);
            FarAreaOwnership.Release(manager, refPosition, uid);
        }

        // Only while that pass is choosing owners: a player claiming a creature from its own game has it loaded already.
        // That pass would give a creature the server keeps to the nearest player. What a dedicated server owns in the zones it
        // generates ahead of players, creatures and their spawn points included, goes to a player like anything else and
        // spawns when one arrives.
        private static bool KeepCreatureEligible(ZDO __instance, long uid)
        {
            if (!Arbitrating || !CreaturePrefabs.IsCreature(__instance)) return true;
            if (CreatureOwnership.ServerKeeps(__instance)) return false;
            return uid == 0L || CreatureOwnership.CanOwn(__instance, uid);
        }
    }
}
