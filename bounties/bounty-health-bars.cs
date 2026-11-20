using EpicLoot.Adventure;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot gives a bounty's target the boss bar at the top of the screen. It keeps that bar within the boss range once
    // shown, as a Nemesis miniboss does, and also shows its name and health above it.
    [HarmonyPatch(typeof(BountyTarget), nameof(BountyTarget.Reinitialize))]
    internal static class BountyHealthBars
    {
        private static void Postfix(BountyTarget __instance)
        {
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) return;
            var character = __instance.GetComponent<Character>();
            if (!character.m_boss) return;
            character.m_dontHideBossHud = true;
            BossOverheadHealthBars.Add(character);
        }
    }
}
