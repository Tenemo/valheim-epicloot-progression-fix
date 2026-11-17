using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A progression boss's stars follow the personal kills of the player who summoned it instead of the world's zone,
    // distance and night bonuses: on average 4 stars plus 1 for every 10 earlier kills of that boss, rolled once when it
    // first appears within 2 stars either side, never under 4 or over 200 stars, so the average holds near both limits.
    // Without an offering, the player nearest the boss when it appears counts. Kills are the game's own, which credit
    // every player who damaged the boss. A boss keeps its saved stars, and Kall Fimbulbringer's last phase takes over the
    // first phase's.
    internal static class BossStars
    {
        internal const int BaseStars = 4, KillsPerStar = 10, Spread = 2, MaxStars = 200;
        // Each boss with the phase whose death counts as defeating it.
        private static readonly Dictionary<string, string> Bosses = new Dictionary<string, string>
        {
            ["Eikthyr"] = "Eikthyr", ["gd_king"] = "gd_king", ["Bonemass"] = "Bonemass", ["Dragon"] = "Dragon", ["GoblinKing"] = "GoblinKing",
            ["SeekerQueen"] = "SeekerQueen", ["Fader"] = "Fader", ["FrozenKing"] = "FrozenKing_p3",
        };
        // Kall Fimbulbringer's last phase takes over the first phase's stars, so it shares the boss cap.
        private static readonly HashSet<string> Capped = new HashSet<string>(Bosses.Keys) { "FrozenKing_p3" };
        internal static readonly string KillsKey = Plugin.Id + ".boss-kills.";
        internal static readonly string SummonerKey = Plugin.Id + ".boss-summoner";
        private static readonly ConditionalWeakTable<OfferingBowl, object> Summoners = new ConditionalWeakTable<OfferingBowl, object>();
        private static long spawningFor;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var selection = StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.LevelSystem.LevelSelection", true);
            var determine = AccessTools.Method(selection, "DetermineLevel", new[] { typeof(Character), typeof(ZDO),
                StarLevelSystem.Assembly.GetType("StarLevelSystem.common.DataObjects+CreatureSpecificSetting", true),
                StarLevelSystem.Assembly.GetType("StarLevelSystem.common.DataObjects+BiomeSpecificSetting", true), typeof(Heightmap.Biome), typeof(int), typeof(bool) });
            var maximum = AccessTools.Method(selection, "GetMaxCreatureLevel");
            if (determine?.ReturnType != typeof(int) || maximum?.ReturnType != typeof(int) || maximum.GetParameters().FirstOrDefault()?.ParameterType != typeof(Character))
                throw new InvalidOperationException("StarLevelSystem's level selection changed.");
            harmony.Patch(determine, prefix: new HarmonyMethod(typeof(BossStars), nameof(BeforeLevel)), postfix: new HarmonyMethod(typeof(BossStars), nameof(RollLevel)));
            harmony.Patch(maximum, postfix: new HarmonyMethod(typeof(BossStars), nameof(RaiseCap)));
        }

        // The stars a player's earlier kills give, as the lowest and highest a roll can give.
        internal static (int Low, int High) Range(int kills)
        {
            int average = Mathf.Min(BaseStars + Mathf.Max(0, kills) / KillsPerStar, MaxStars);
            int spread = Mathf.Min(Spread, Mathf.Min(average - BaseStars, MaxStars - average));
            return (average - spread, average + spread);
        }

        private static bool IsBoss(Character character, out string killedAs)
        {
            killedAs = null;
            return character && Bosses.TryGetValue(Utils.GetPrefabName(character.gameObject), out killedAs);
        }

        // Takes the native DetermineLevel arguments: a boss with no saved level that its owner rolls now.
        private static void BeforeLevel(Character __0, ZDO __1, int __5, bool __6, out bool __state) =>
            __state = __6 && __5 <= 0 && __1 != null && __1.GetInt(ZDOVars.s_level) <= 0 && IsBoss(__0, out _);

        private static void RollLevel(Character __0, ZDO __1, ref int __result, bool __state)
        {
            if (!__state || !IsBoss(__0, out string killedAs)) return;
            var (low, high) = Range(KillsOf(__0, __1, killedAs));
            // A level is stars + 1. Saving it at once keeps every later read of this boss on the same roll.
            __result = UnityEngine.Random.Range(low, high + 1) + 1;
            __1.Set(ZDOVars.s_level, __result);
        }

        private static void RaiseCap(Character __0, ref int __result)
        {
            if (__0 && Capped.Contains(Utils.GetPrefabName(__0.gameObject))) __result = Mathf.Max(__result, MaxStars + 1);
        }

        // The summoner's published kills, or those of the player nearest the boss. SLS may set the boss up as it appears,
        // before its record holds the summoner.
        private static int KillsOf(Character boss, ZDO data, string killedAs)
        {
            long summoner = data.GetLong(SummonerKey, spawningFor);
            var players = Player.GetAllPlayers().Where(player => player && player.GetComponent<ZNetView>()?.GetZDO() != null).ToList();
            var counted = players.FirstOrDefault(player => summoner != 0 && player.GetComponent<ZNetView>().GetZDO().GetOwner() == summoner) ??
                players.OrderBy(player => Vector3.Distance(player.transform.position, boss.transform.position)).FirstOrDefault();
            return counted ? counted.GetComponent<ZNetView>().GetZDO().GetInt(KillsKey + killedAs) : 0;
        }

        // The local character's kills of each boss, as the game counts them, on its own record for whoever spawns one.
        internal static void Publish()
        {
            var player = Player.m_localPlayer;
            var data = player ? player.GetComponent<ZNetView>()?.GetZDO() : null;
            if (data == null || Game.instance == null || ZNetScene.instance == null) return;
            var kills = Game.instance.GetPlayerProfile().m_playerStats[0].m_enemyStats[0];
            foreach (string killedAs in Bosses.Values.Distinct())
            {
                string name = ZNetScene.instance.GetPrefab(killedAs)?.GetComponent<Character>()?.m_name;
                int count = name != null && kills.TryGetValue(name, out float value) ? Mathf.FloorToInt(value) : 0;
                if (data.GetInt(KillsKey + killedAs) != count) data.Set(KillsKey + killedAs, count);
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Spawned
        {
            private static void Postfix(Player __instance)
            {
                if (__instance == Player.m_localPlayer) Publish();
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.RPC_RegisterKill))]
        private static class Killed
        {
            private static void Postfix() => Publish();
        }

        // The bowl's owner spawns the boss a few seconds after the summoner's request; it records who asked.
        [HarmonyPatch(typeof(OfferingBowl), "RPC_SpawnBoss")]
        private static class Summoned
        {
            private static void Prefix(OfferingBowl __instance, out bool __state) => __state = __instance.IsInvoking("DelayedSpawnBoss");

            private static void Postfix(OfferingBowl __instance, long senderId, bool __state)
            {
                if (__state || !__instance.IsInvoking("DelayedSpawnBoss")) return;
                Summoners.Remove(__instance);
                Summoners.Add(__instance, senderId);
            }
        }

        [HarmonyPatch(typeof(OfferingBowl), "DelayedSpawnBoss")]
        private static class Spawning
        {
            private static void Prefix(OfferingBowl __instance) => spawningFor = Summoners.TryGetValue(__instance, out object summoner) ? (long)summoner : 0;

            private static void Finalizer() => spawningFor = 0;
        }

        // The boss appears inside DelayedSpawnBoss; its summoner goes on its record before SLS sets its level.
        [HarmonyPatch(typeof(Character), "Awake")]
        private static class Appeared
        {
            private static void Postfix(Character __instance)
            {
                if (spawningFor == 0 || !IsBoss(__instance, out _)) return;
                var view = __instance.GetComponent<ZNetView>();
                if (view && view.IsValid() && view.IsOwner()) view.GetZDO().Set(SummonerKey, spawningFor);
            }
        }
    }
}
