using System;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The game has Ashlands heat wear built in, but its main scene sets the rate to 0. The synced setting sets it: the game
    // that owns a piece, normally a nearby player's, takes that percent of the piece's maximum health about every 6 seconds
    // while the piece has nothing above it and is outside every shield generator; ash-immune pieces, private chests holding
    // items and ships with someone aboard are exempt. Pieces that can't catch fire, which cinders can't set alight either,
    // ships and carts, and planted Ashvines and saplings are spared too. What a piece stands on and its own parts are not
    // cover.
    internal static class AshlandsAmbientWear
    {
        // The ash wear step skips a piece the game believes has cover above it. Resolved at load, so a renamed field stops
        // the add-on instead of letting stone, ships and carts wear.
        private static readonly AccessTools.FieldRef<WearNTear, bool> AshRoof = AccessTools.FieldRefAccess<WearNTear, bool>("m_haveAshRoof");

        private static void Apply(object sender, EventArgs arguments)
        {
            if (Game.instance != null) Game.instance.m_ashDamage = Plugin.AshlandsBuildingWear.Value;
        }

        [HarmonyPatch(typeof(Game), "Awake")]
        private static class Rate
        {
            private static void Postfix()
            {
                Apply(null, null);
                Plugin.AshlandsBuildingWear.SettingChanged -= Apply;
                Plugin.AshlandsBuildingWear.SettingChanged += Apply;
            }
        }

        // Only an Ashlands piece the game found uncovered has the flag cleared, so other pieces stop at the first check.
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateWear))]
        private static class Spared
        {
            private static bool Prepare() => AshRoof != null;

            private static void Prefix(WearNTear __instance)
            {
                ref bool covered = ref AshRoof(__instance);
                if (!covered && (!__instance.m_burnable || __instance.GetComponent<Ship>() || __instance.GetComponent<Vagon>()
                    || __instance.GetComponent<Vine>() || __instance.GetComponent<Plant>())) covered = true;
            }
        }

        // The game's cover check casts a 0.1 m sphere up from the piece's pivot and skips only the piece and its direct
        // children, so a piece whose pivot rests on a floor, such as a hot tub or a blast furnace, or whose colliders sit deeper
        // counted as its own roof. The same cast starts 0.15 m higher, clear of what the piece stands on, and skips everything
        // inside the piece; a roof found stays remembered until it is gone, as in the game.
        [HarmonyPatch(typeof(WearNTear), "HaveAshRoof")]
        private static class Cover
        {
            private static readonly AccessTools.FieldRef<WearNTear, GameObject> Roof = AccessTools.FieldRefAccess<WearNTear, GameObject>("m_ashroof");
            private static readonly AccessTools.FieldRef<int> Mask = AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(WearNTear), "s_rayMask"));
            private static readonly RaycastHit[] Hits = new RaycastHit[128];

            private static bool Prefix(WearNTear __instance, ref bool __result)
            {
                ref GameObject roof = ref Roof(__instance);
                if (!roof)
                {
                    var piece = __instance.transform;
                    int count = Physics.SphereCastNonAlloc(piece.position + Vector3.up * 0.15f, 0.1f, Vector3.up, Hits, 100f, Mask());
                    for (int index = 0; index < count && !roof; index++)
                        if (!Hits[index].collider.transform.IsChildOf(piece)) roof = Hits[index].collider.gameObject;
                }
                __result = roof;
                return false;
            }
        }
    }
}
