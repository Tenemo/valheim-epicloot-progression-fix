using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot's parry bonus (ModifyParry) adds to a shield's base block power during the parry window, and the game
    // then multiplies that block power by the shield's own parry bonus.
    [HarmonyPatch]
    internal static class ParryBonus
    {
        // EpicLoot treats the start of every block as a parry, so shields that cannot parry, such as tower shields,
        // also got ModifyParry's extra block power and block force there.
        [HarmonyPatch(typeof(ModifyParryWindow), nameof(ModifyParryWindow.IsParry)), HarmonyPostfix]
        private static void OnlyShieldsThatParry(ref bool __result)
        {
            if (!__result) return;
            var blocker = BlockedHits.CurrentBlocker(Player.m_localPlayer);
            if (blocker == null || blocker.m_shared.m_timedBlockBonus <= 1f) __result = false;
        }
    }
}
