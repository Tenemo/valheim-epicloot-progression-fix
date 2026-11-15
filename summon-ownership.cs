using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class SummonOwnership
    {
        private const string SummonerKey = Plugin.Id + ".summoner";
        private const string StaffKey = Plugin.Id + ".summoning-staff";
        // The ZDO's string-keyed reads hash the key on every call; bars ask these of every tamed creature each frame.
        private static readonly int SummonerHash = SummonerKey.GetStableHashCode(), StaffHash = StaffKey.GetStableHashCode(),
            RaisedByHash = "el-sumby".GetStableHashCode(), SpiritHash = "el-spirit".GetStableHashCode();
        internal const string SkeletonStaff = "StaffSkeleton", SpiritStaff = "StaffSpiritCaller";
        private static readonly Dictionary<int, string> StaffTypes = new Dictionary<string, string>
        {
            ["Skeleton_Friendly"] = SkeletonStaff,
            ["Bjorn_spiritcaller"] = SpiritStaff,
            ["Moose_spiritcaller"] = SpiritStaff,
            ["Wolf_spiritcaller"] = SpiritStaff,
            ["Boar_spiritcaller"] = SpiritStaff,
            ["staff_greenroots_tentaroot"] = "StaffGreenRoots",
            ["Troll_Summoned"] = "StaffRedTroll"
        }.ToDictionary(pair => pair.Key.GetStableHashCode(), pair => pair.Value);

        internal static void Initialize(IntegrationPatches harmony)
        {
            // SetupAoe receives every created character, including spawns that have no accompanying AOE.
            harmony.Patch(AccessTools.Method(typeof(SpawnAbility), "SetupAoe"),
                postfix: new HarmonyMethod(typeof(SummonOwnership), nameof(RecordCreation)));
            harmony.Patch(AccessTools.Method(typeof(MonsterAI), nameof(MonsterAI.SetFollowTarget)),
                postfix: new HarmonyMethod(typeof(SummonOwnership), nameof(RememberFollow)));
            harmony.Patch(AccessTools.Method(typeof(Tameable), "UnsummonMaxInstances"),
                transpiler: new HarmonyMethod(typeof(SummonOwnership), nameof(ShareSpiritLimit)));
            harmony.Patch(AccessTools.Method(typeof(Tameable), "UnsummonMaxInstances"),
                transpiler: new HarmonyMethod(typeof(SummonOwnership), nameof(ExplainLimit)));
        }

        // The game's follower limit counts each creature name apart; all Spirit Caller animals count as one kind.
        private static IEnumerable<CodeInstruction> ShareSpiritLimit(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.LoadsField(AccessTools.Field(typeof(Character), nameof(Character.m_name))),
                AccessTools.Method(typeof(SummonOwnership), nameof(SummonKind)), 2, "The native summon-limit census changed.");

        private static string SummonKind(Character character) =>
            StaffFor(GameFields.CharacterView(character)?.GetZDO()) == SpiritStaff ? SpiritStaff : character.m_name;

        // The game shows its notice after it has already sent the oldest summon away for the new one, but words it as if the
        // cast was refused. Spirit Caller animals and Dead Raiser skeletons say what happened; other summons keep the game's text.
        private static IEnumerable<CodeInstruction> ExplainLimit(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.LoadsField(AccessTools.Field(typeof(Tameable), nameof(Tameable.m_maxSummonReached))),
                AccessTools.Method(typeof(SummonOwnership), nameof(LimitNotice)), 1, "The native summon-limit notice changed.");

        private static string LimitNotice(Tameable tame)
        {
            var data = tame.GetComponent<ZNetView>()?.GetZDO();
            string staff = StaffFor(data);
            int limit = data?.GetInt(ZDOVars.s_maxInstances) ?? 0;
            if (limit < 1 || staff != SpiritStaff && staff != SkeletonStaff) return tame.m_maxSummonReached;
            return staff == SpiritStaff ? $"Your Spirit Caller holds {limit} {(limit == 1 ? "animal" : "animals")}, so your oldest one left."
                : $"Your Dead Raiser holds {limit} {(limit == 1 ? "skeleton" : "skeletons")}, so your oldest one left.";
        }

        private static void RecordCreation(Character __0, Character ___m_owner, ItemDrop.ItemData ___m_weapon)
        {
            if (!(___m_owner is Player player) || ___m_weapon?.m_dropPrefab == null) return;
            var view = __0 ? GameFields.CharacterView(__0) : null;
            if (!view || !view.IsValid() || !view.IsOwner() || !StaffTypes.ContainsKey(view.GetZDO().GetPrefab())) return;
            view.GetZDO().Set(SummonerKey, player.GetPlayerID());
            view.GetZDO().Set(StaffKey, ___m_weapon.m_dropPrefab.name);
        }

        private static void RememberFollow(MonsterAI __instance, GameObject __0)
        {
            var player = __0 ? __0.GetComponent<Player>() : null;
            if (player) TryGet(__instance.GetComponent<Character>(), out _, out _, player);
        }

        internal static bool IsFollower(Character character)
        {
            var view = character ? GameFields.CharacterView(character) : null;
            return character && character.IsTamed() && view && view.IsValid() &&
                StaffTypes.TryGetValue(view.GetZDO().GetPrefab(), out var staff) && (staff == SkeletonStaff || staff == SpiritStaff);
        }

        internal static bool TryGet(Character character, out long summoner, out string staff, Player following = null)
        {
            summoner = 0; staff = null;
            var view = character ? GameFields.CharacterView(character) : null;
            if (!view || !view.IsValid() || !StaffTypes.TryGetValue(view.GetZDO().GetPrefab(), out string expected)) return false;
            var data = view.GetZDO();
            summoner = data.GetLong(SummonerHash);
            staff = data.GetString(StaffHash);
            if (summoner != 0) return !string.IsNullOrEmpty(staff);
            if (!IsFollower(character)) return false;
            string name = data.GetString(ZDOVars.s_follow);
            if (!string.IsNullOrEmpty(name))
            {
                var players = Player.GetAllPlayers().Where(player => player.GetPlayerName() == name).Take(2).ToArray();
                if (players.Length != 1 || following && following != players[0]) return false;
                following = players[0];
            }
            if (!following) following = character.GetComponent<MonsterAI>()?.GetFollowTarget()?.GetComponent<Player>();
            if (!following) return false;
            summoner = following.GetPlayerID(); staff = expected;
            if (view.IsOwner())
            {
                data.Set(SummonerKey, summoner);
                data.Set(StaffKey, staff);
            }
            return true;
        }

        // A creature a player summoned: with a staff, one of the bats a shardstone calls, or one EpicLoot records as a player's,
        // such as a Spirit Animal. The game also counts most of them as tamed.
        internal static bool IsPlayerSummon(Character character)
        {
            var view = character ? GameFields.CharacterView(character) : null;
            var data = view && view.IsValid() ? view.GetZDO() : null;
            return data != null && (StaffFor(data) != null || SummonedBats.SummonerId(data) != 0 || data.GetLong(RaisedByHash, 0L) != 0 || data.GetLong(SpiritHash, 0L) != 0);
        }
        // A summon of the local player other than a shardstone bat: a staff summon, a Hel's Harvest raise or a Spirit Animal.
        // A caller that has just found the character is no bat says so instead of having it looked up again.
        internal static bool OwnSummon(Character character, bool notBat = false)
        {
            var player = Player.m_localPlayer;
            if (!player || !character || !character.IsTamed() || !notBat && SummonedBats.IsBat(character)) return false;
            var data = GameFields.CharacterView(character).GetZDO();
            long id = player.GetPlayerID();
            // A tamed creature's view is valid, so TryGet can only find a record on a staff's kind of creature.
            return data.GetLong(RaisedByHash, 0L) == id || data.GetLong(SpiritHash, 0L) == id ||
                StaffTypes.ContainsKey(data.GetPrefab()) && TryGet(character, out long summoner, out _) && summoner == id;
        }
        internal static Player Summoner(Character character) => TryGet(character, out long player, out _) ? Player.GetPlayer(player) : null;
        // The player who summoned any kind of player summon, or 0.
        internal static long SummonerId(Character character)
        {
            long bat = SummonedBats.SummonerId(character);
            if (bat != 0 || !character || !character.IsTamed()) return bat;
            var data = GameFields.CharacterView(character).GetZDO();
            long raised = data.GetLong(RaisedByHash, 0L);
            if (raised != 0) return raised;
            long spirit = data.GetLong(SpiritHash, 0L);
            if (spirit != 0) return spirit;
            return StaffTypes.ContainsKey(data.GetPrefab()) && TryGet(character, out long summoner, out _) ? summoner : 0;
        }
        internal static bool IsStaff(string prefab) => StaffTypes.ContainsValue(prefab);
        internal static string StaffFor(ZDO data) => data != null && StaffTypes.TryGetValue(data.GetPrefab(), out var staff) ? staff : null;
        internal static bool Recorded(ZDO data, long player, string staff) => data.GetLong(SummonerHash) == player && data.GetString(StaffHash) == staff;
        internal static bool Unrecorded(ZDO data) => data.GetLong(SummonerHash) == 0;
    }
}
