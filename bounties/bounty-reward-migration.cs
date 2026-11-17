using EpicLoot.Adventure;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot 0.15 doubled the offer table but saved accepted contracts keep their old amounts. Match the complete
    // reward tuple and target, not acceptance time or remaining escorts, whose counts decrease as they die. Current
    // rewards win over a legacy match, so repeating a lookup or reloading the character never doubles a current contract.
    [HarmonyPatch(typeof(PlayerExtensions_Adventure), nameof(PlayerExtensions_Adventure.GetAdventureSaveData))]
    internal static class BountyRewardMigration
    {
        private static void Postfix(Player player, AdventureSaveData __result)
        {
            if (player != Player.m_localPlayer || ZNet.instance == null || ZNet.instance.GetWorldUID() == 0 || __result == null) return;
            var targets = AdventureDataManager.Config?.Bounties?.Targets;
            if (targets == null) return;
            long owner = player.GetPlayerID(); bool changed = false;
            foreach (var bounty in __result.Bounties)
            {
                if (bounty.PlayerID != owner || bounty.Target == null ||
                    (bounty.State != BountyState.InProgress && bounty.State != BountyState.Complete)) continue;
                BountyTargetConfig current = null;
                foreach (var target in targets)
                {
                    if (target.GetBiome() != bounty.Biome || target.TargetID != bounty.Target.MonsterID) continue;
                    if (Matches(bounty, target, 1)) { current = null; break; }
                    if (Matches(bounty, target, 2)) current = target;
                }
                if (current == null) continue;
                bounty.RewardCoins = current.RewardCoins; bounty.RewardIron = current.RewardIron; bounty.RewardGold = current.RewardGold;
                changed = true;
            }
            if (changed) player.SaveAdventureSaveData();
        }

        private static bool Matches(BountyInfo bounty, BountyTargetConfig target, int multiplier) =>
            bounty.RewardCoins >= 0 && bounty.RewardIron >= 0 && bounty.RewardGold >= 0 &&
            (long)bounty.RewardCoins * multiplier == target.RewardCoins &&
            (long)bounty.RewardIron * multiplier == target.RewardIron &&
            (long)bounty.RewardGold * multiplier == target.RewardGold;
    }
}
