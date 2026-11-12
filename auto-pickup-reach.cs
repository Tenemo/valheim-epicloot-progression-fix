using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Auto pickup collects the colliders on the item layer around the player into a fixed list of 100 and ignores the rest.
    // Many pickables share that layer: mushrooms, dandelions, thistle and the like. A garden inside EpicLoot's larger pickup
    // range filled the list, so dropped items there moved a few steps toward the player and then lay still, even beside them.
    // A pass that fills the list grows it for the next one, so a crowded spot misses items for one physics step at most.
    [HarmonyPatch(typeof(Player), "AutoPickup")]
    internal static class AutoPickupReach
    {
        private static void Postfix(ref Collider[] ___m_colliders)
        {
            if (___m_colliders.Length > 0 && ___m_colliders[___m_colliders.Length - 1] != null)
                ___m_colliders = new Collider[___m_colliders.Length * 4];
        }
    }
}
