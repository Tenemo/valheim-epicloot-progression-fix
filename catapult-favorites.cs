using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The catapult launches whatever hotbar or quick-slot item is used on its load point. A favorited item, or one in a
    // favorited slot, is refused the way the game refuses an item that won't fit, so the key press falls through to the
    // item's own use.
    [HarmonyPatch(typeof(Catapult), "OnLoadPointUse")]
    internal static class CatapultFavorites
    {
        private static bool Prefix(Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (item == null || !(user is Player player) || player != Player.m_localPlayer || !FavoriteProtection.IsFavorite(player, item)) return true;
            player.Message(MessageHud.MessageType.Center, "Favorited items can't be loaded into the catapult.");
            player.UseIemBlockkMessage();
            __result = false;
            return false;
        }
    }
}
