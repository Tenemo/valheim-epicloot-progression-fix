using System.Linq;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Tamed animals that fight and the followers (Dead Raiser skeletons, Spirit Caller animals and Fylgja spirits) pick, keep
    // and chase enemies, follow and keep pace by the same rules. A follower fights for its summoner, a tame for the player it
    // follows; a tame that follows no one measures from the spot it stays at.
    internal static class Companions
    {
        internal static bool IsCompanion(Character character) => SummonOwnership.CompanionOf(character) != SummonOwnership.Companion.None;

        // A tamed creature with a fighting AI that no player summoned: shardstone bats, Hel's Harvest raises, Trollstav trolls and
        // tentaroots keep their own rules.
        internal static bool IsTame(Character character) => SummonOwnership.CompanionOf(character) == SummonOwnership.Companion.Tame;

        // The same from saved data alone, as the server reads creatures it doesn't run.
        internal static bool IsTame(ZDO data) => data != null && data.GetBool(ZDOVars.s_tamed) && !SummonOwnership.IsPlayerSummon(data) &&
            ZNetScene.instance?.GetPrefab(data.GetPrefab())?.GetComponent<MonsterAI>();

        internal static Player Leader(Character character) => Leader(character, SummonOwnership.CompanionOf(character));

        // The player a companion fights for: a follower's summoner, or the living player a tame follows.
        internal static Player Leader(Character character, SummonOwnership.Companion kind)
        {
            if (kind == SummonOwnership.Companion.Follower) return SummonOwnership.Summoner(character);
            if (kind != SummonOwnership.Companion.Tame) return null;
            var followed = (character.GetBaseAI() as MonsterAI).GetFollowTarget();
            var player = followed ? followed.GetComponent<Player>() : null;
            if (!player)
            {
                // Only the creature's owner sets the follow target; other games find the player by the saved name.
                string name = GameFields.CharacterView(character).GetZDO().GetString(ZDOVars.s_follow);
                if (name.Length > 0)
                {
                    var players = Player.GetAllPlayers().Where(candidate => candidate.GetPlayerName() == name).Take(2).ToArray();
                    if (players.Length == 1) player = players[0];
                }
            }
            return player && !player.IsDead() ? player : null;
        }
    }
}
