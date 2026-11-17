using System.Collections.Generic;
using System.Linq;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // EpicLoot 0.14.13 compares its payout roll with a second random number and scales that roll only above a combined
    // Riches of 100%, so below it any Riches at all gave treasure on about half of all deaths and more Riches added
    // nothing. It also truncates each player's total to a whole percent and reuses one total for five seconds of deaths
    // anywhere. Roll the combined Riches of the players within the shared loot radius, each counted in full, as the
    // chance, and above 100% raise the payout budget in proportion. The treasure still comes from EpicLoot's configured
    // table: one kind picked at random, as many as the budget buys, or the cheapest kind when it buys none.
    [HarmonyPatch(typeof(Riches_CharacterDrop_GenerateDropList_Patch), "Postfix")]
    internal static class RichesRoll
    {
        // Each game publishes its own player's unrounded total here; EpicLoot's "el-rch" keeps the truncated one.
        internal static readonly int TotalKey = "epicloot_progression_riches".GetStableHashCode();

        // Positional arguments refer to the patched static postfix's CharacterDrop and drop list parameters.
        private static bool Prefix(CharacterDrop __0, ref List<KeyValuePair<GameObject, int>> __1)
        {
            var players = new List<Player>();
            Player.GetPlayersInRange(__0.transform.position, Plugin.LootBonusRadius.Value, players);
            float total = 0f;
            foreach (var player in players)
            {
                var view = GameFields.CharacterView(player);
                if (view != null && view.IsValid()) total += view.GetZDO().GetFloat(TotalKey);
            }
            float chance = total / 100f;
            if (chance <= 0f || chance < 1f && UnityEngine.Random.value >= chance) return false;

            var table = Riches_CharacterDrop_GenerateDropList_Patch.RichesTable;
            if (table.Count == 0)
            {
                Riches_CharacterDrop_GenerateDropList_Patch.UpdateRichesOnEffectSetup();
                table = Riches_CharacterDrop_GenerateDropList_Patch.RichesTable;
                if (table.Count == 0) return false;
            }
            float budget = UnityEngine.Random.value * 100f * Mathf.Max(1f, chance);
            var treasure = table.ElementAt(UnityEngine.Random.Range(0, table.Count));
            float ratio = budget / treasure.Value;
            int amount = ratio < 1f ? 0 : Mathf.RoundToInt(ratio);
            var prefab = treasure.Key;
            var cheapest = Riches_CharacterDrop_GenerateDropList_Patch.LowestCostEntry;
            if (amount == 0 && cheapest.Key != null)
            {
                amount = Mathf.RoundToInt(budget / cheapest.Value);
                prefab = cheapest.Key;
            }
            if (amount >= 1) __1.Add(new KeyValuePair<GameObject, int>(prefab, amount));
            return false;
        }
    }

    // EpicLoot publishes a player's Riches only when equipment changes, so augmenting a worn item, or putting a
    // shardstone or rune in it, kept the old value. Check the local player's total once a second and publish it when it
    // changed.
    [HarmonyPatch(typeof(Player), "Update")]
    internal static class RichesTotal
    {
        private static float nextCheck;

        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer || Time.time < nextCheck) return;
            nextCheck = Time.time + 1f;
            var view = GameFields.CharacterView(__instance);
            if (view == null || !view.IsValid()) return;
            float total = __instance.GetTotalActiveMagicEffectValue(MagicEffectType.Riches);
            var zdo = view.GetZDO();
            if (zdo.GetFloat(RichesRoll.TotalKey) != total) zdo.Set(RichesRoll.TotalKey, total);
        }
    }
}
