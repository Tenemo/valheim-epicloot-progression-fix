using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using EpicLoot_UnityLib;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot gives a table the saved field of each upgrade feature only when the table starts on the game that owns it,
    // and reads a missing field as an unavailable feature. A table saved before Storage existed and since started only on
    // games that did not own it hides its Storage upgrade for good. The server now fills every missing field of an active
    // feature with its configured starting level when it loads the world, before anyone can join, and a game that takes
    // a loaded table over does the same; existing levels stay.
    [HarmonyPatch]
    internal static class EnchantingTableFeatures
    {
        private static readonly int TablePrefab = EnchantingTable.PrefabName.GetStableHashCode();
        private static readonly EnchantingFeature[] Features = (EnchantingFeature[])Enum.GetValues(typeof(EnchantingFeature));
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> Objects =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");
        private static readonly AccessTools.FieldRef<EnchantingTable, ZNetView> View = AccessTools.FieldRefAccess<EnchantingTable, ZNetView>("_nview");
        // Loaded tables this game has filled since it last took them over.
        private static readonly ConditionalWeakTable<EnchantingTable, object> Owned = new ConditionalWeakTable<EnchantingTable, object>();
        private static readonly object Filled = new object();
        private static ManualLogSource log;

        internal static void Initialize(ManualLogSource logger) => log = logger;

        // EpicLoot's own starting rule, which also replaces a saved unavailable mark, for active features only. Returns how
        // many fields it filled; a repeated call fills none.
        internal static int Fill(ZDO table)
        {
            int filled = 0;
            foreach (var feature in Features)
            {
                int start = EnchantingTable.GetDefaultFeatureLevel(feature);
                string field = "el.et.v1." + feature;
                if (start == EnchantingTable.FeatureUnavailableSentinel || table.GetInt(field, -1) >= 0) continue;
                table.Set(field, start + 1);
                filled++;
            }
            return filled;
        }

        [HarmonyPatch(typeof(ZNet), "ServerLoadWorld"), HarmonyPostfix]
        private static void LoadWorld()
        {
            int tables = 0, fields = 0, repaired = 0;
            foreach (var data in Objects(ZDOMan.instance).Values)
            {
                if (data.GetPrefab() != TablePrefab) continue;
                tables++;
                int filled = Fill(data);
                fields += filled;
                if (filled > 0) repaired++;
            }
            log.LogInfo($"Enchanting tables: filled {fields} missing upgrade fields on {repaired} of {tables} saved tables.");
        }

        [HarmonyPatch(typeof(EnchantingTable), nameof(EnchantingTable.Update)), HarmonyPostfix]
        private static void TakeOver(EnchantingTable __instance)
        {
            var view = View(__instance);
            if (view == null || !view.IsValid()) return;
            if (!view.IsOwner()) { Owned.Remove(__instance); return; }
            if (Owned.TryGetValue(__instance, out _)) return;
            Owned.Add(__instance, Filled);
            // EpicLoot's next update sees the changed data and refreshes the table's levels and its open window.
            Fill(view.GetZDO());
        }
    }
}
