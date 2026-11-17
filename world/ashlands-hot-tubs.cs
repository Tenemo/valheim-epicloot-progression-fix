using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // A hot tub standing in the Ashlands is heated by the land. The game builds the tub as a smelter that burns wood and
    // makes nothing; here its owner keeps its wood full instead of burning it, so it stays lit, warm and cozy for everyone,
    // and adding wood in any way only says so and takes nothing. Hot tubs elsewhere are unchanged.
    [HarmonyPatch]
    internal static class AshlandsHotTubs
    {
        internal const string Message = "It's hot enough here already!";
        private static readonly int Bathtub = "piece_bathtub".GetStableHashCode();
        private static readonly ConditionalWeakTable<Smelter, object> Heat = new ConditionalWeakTable<Smelter, object>();

        // Pieces don't move, so each tub looks up the generated world's biome once; unlike the loaded terrain, it is always there.
        internal static bool Heated(Smelter tub)
        {
            if (Heat.TryGetValue(tub, out object known)) return (bool)known;
            var view = tub.GetComponent<ZNetView>();
            var data = view ? view.GetZDO() : null;
            if (data == null || WorldGenerator.instance == null) return false;
            bool heated = data.GetPrefab() == Bathtub && WorldGenerator.instance.GetBiome(tub.transform.position) == Heightmap.Biome.AshLands;
            Heat.Add(tub, heated);
            return heated;
        }

        // Asked by the owner's smelter step (StationSteps): true burns fuel as in the game, false stops after the lit state.
        internal static bool BurnsFuel(ZNetView view, Smelter tub)
        {
            if (!Heated(tub)) return true;
            // An unchanged value is not a change, so a full tub sends nothing.
            view.GetZDO().Set(ZDOVars.s_fuel, (float)tub.m_maxFuel);
            return false;
        }

        // The heat isn't wood, so a removed or destroyed tub drops none.
        [HarmonyPatch(typeof(Smelter), "OnDestroyed"), HarmonyPrefix]
        private static void NoWood(Smelter __instance)
        {
            if (Heated(__instance)) __instance.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_fuel, 0f);
        }

        // The Use key, holding it and wood from the hotbar all come here. First, so bulk filling never starts.
        [HarmonyPatch(typeof(Smelter), "OnAddFuel"), HarmonyPrefix, HarmonyPriority(Priority.First + 1)]
        private static bool AddFuel(Smelter __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (!Heated(__instance) || item != null && item.m_shared.m_name != __instance.m_fuelItem.m_itemData.m_shared.m_name) return true;
            user.Message(MessageHud.MessageType.Center, Message);
            // Handled, so held wood isn't tried any other way.
            __result = item != null;
            return false;
        }

        // The hover item menu offers no wood.
        [HarmonyPatch(typeof(Smelter), nameof(Smelter.TryGetItems)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool MenuItems(Smelter __instance, Player player, Switch switchRef, ref List<string> items, ref bool __result)
        {
            if (!Heated(__instance) || switchRef != __instance.m_addWoodSwitch) return true;
            items = new List<string>();
            player.Message(MessageHud.MessageType.Center, Message);
            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(Smelter), nameof(Smelter.CanUseItems)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool MenuUse(Smelter __instance, Player player, Switch switchRef, bool sendErrorMessage, ref bool __result)
        {
            if (!Heated(__instance) || switchRef != __instance.m_addWoodSwitch) return true;
            if (sendErrorMessage) player.Message(MessageHud.MessageType.Center, Message);
            __result = false;
            return false;
        }

        // Only the name: there is no wood to count or add.
        [HarmonyPatch(typeof(Smelter), "OnHoverAddFuel"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool Hover(Smelter __instance, ref string __result)
        {
            if (!Heated(__instance)) return true;
            __result = Localization.instance.Localize(__instance.m_name);
            return false;
        }
    }
}
