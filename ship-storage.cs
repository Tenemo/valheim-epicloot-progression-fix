using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Container), "Awake")]
    internal static class ShipStorage
    {
        private static void Prefix(Container __instance)
        {
            var ship = __instance.GetComponentInParent<Ship>();
            if (!ship) return;
            // Keep native columns so saved cargo retains its cells when these extra rows become available.
            switch (Utils.GetPrefabName(ship.gameObject))
            {
                case "Karve":
                case "VikingShip": __instance.m_height = 4; break;
                case "VikingShip_Ashlands": __instance.m_height = 5; break;
            }
        }
    }
}
