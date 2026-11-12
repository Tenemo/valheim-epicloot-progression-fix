using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Preserve the incoming skill-adjusted duration for either reload branch, applying Quick Draw once.
    [HarmonyPatch(typeof(Quickdraw_Player_GetWeaponLoadingTime), "Postfix")]
    internal static class CrossbowReload
    {
        private static bool Prefix(ItemDrop.ItemData __0, ref float __1)
        {
            var player = Player.m_localPlayer;
            if (player != null && (__0.m_shared.m_attack.m_requiresReload || __0.m_shared.m_secondaryAttack.m_requiresReload))
                __1 *= 1f - player.GetTotalActiveMagicEffectValue(MagicEffectType.QuickDraw, .01f);
            return false;
        }
    }
}
