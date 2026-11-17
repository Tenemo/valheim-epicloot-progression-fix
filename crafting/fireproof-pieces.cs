using System;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Crystal walls and hot tubs can't catch fire: cinders don't light them, a burning fire's damage passes them by and
    // Ashlands heat wear spares them, since the game and AshlandsAmbientWear read only the prefab's burnable flag for all three.
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    internal static class FireproofPieces
    {
        private static readonly (string Prefab, string Name)[] Pieces = { ("crystal_wall_1x1", "Crystal wall"), ("piece_bathtub", "Hot tub") };

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ZNetScene __instance)
        {
            foreach (var (prefab, name) in Pieces)
            {
                var found = __instance.GetPrefab(prefab);
                var piece = found ? found.GetComponent<WearNTear>() : null;
                if (!piece) throw new InvalidOperationException(name + "'s building component is missing.");
                piece.m_burnable = false;
            }
        }
    }
}
