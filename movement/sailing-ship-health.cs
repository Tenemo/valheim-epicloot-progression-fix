using System;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class SailingShipHealth
    {
        private static Action<WearNTear> addShipBonus;

        internal static void Initialize()
        {
            if (!PluginIds.TryGet(PluginIds.Sailing, out var plugin)) return;
            var placement = plugin.Instance.GetType().GetNestedType("AddZDO", BindingFlags.NonPublic);
            addShipBonus = AccessTools.MethodDelegate<Action<WearNTear>>(AccessTools.Method(placement, "Postfix"));
        }

        // Earlier construction patches can inline the short native callback before Sailing patches it. After the native
        // placement work, run Sailing's unchanged callback only if it was skipped.
        internal static void OnPlaced(WearNTear piece)
        {
            if (addShipBonus != null && piece.GetComponent<Ship>() && !piece.GetComponent<ZNetView>().GetZDO().GetFloat("Sailing Skill Level", out _))
                addShipBonus(piece);
        }
    }
}
