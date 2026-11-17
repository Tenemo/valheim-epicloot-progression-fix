using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Undying restores full health instead of a killing blow, but a burn tick or the next hit could still kill right after.
    // When it fires, the player also gets Bulwark's immunity to combat damage for a few seconds; a Bulwark already up lasts at
    // least that long.
    internal static class UndyingGrace
    {
        internal const float Seconds = 3f;
        private static readonly int BulwarkHash = EpicAssets.Bulwark_SE_Name.GetStableHashCode();

        internal static void Initialize(IntegrationPatches harmony) =>
            harmony.Patch(AccessTools.Method(typeof(UndyingAbility), nameof(UndyingAbility.Trigger)),
                postfix: new HarmonyMethod(typeof(UndyingGrace), nameof(Shield)));

        private static void Shield()
        {
            var player = Player.m_localPlayer;
            var bulwark = ObjectDB.instance ? ObjectDB.instance.GetStatusEffect(BulwarkHash) : null;
            if (!player || !bulwark) return;
            var running = player.GetSEMan().GetStatusEffect(BulwarkHash);
            if (running)
            {
                // Bulwark's own cooldown is kept apart from the effect, so lengthening this instance changes only its time left.
                float remaining = running.GetRemaningTime();
                if (remaining < Seconds) running.m_ttl += Seconds - remaining;
                return;
            }
            var shield = player.GetSEMan().AddStatusEffect(bulwark);
            if (shield) shield.m_ttl = Seconds;
        }
    }
}
