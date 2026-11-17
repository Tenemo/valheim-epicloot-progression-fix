using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Building stays the game's own single-frame action: the piece is created and its materials are taken from the bag
    // and AzuCraftyBoxes' chests in the same frame. The piece records what it cost and the game charges exactly that, so
    // dismantling returns it: the recipe, or nothing for free building. Pieces built while Lucky Craft also saved materials
    // on building refund their smaller recorded cost; pieces built before any record keep the game's full refund.
    [HarmonyPatch]
    internal static class ConstructionPayment
    {
        private const string PaidKey = Plugin.Id + ".paid-construction";
        private static Piece placing;
        private static Piece.Requirement[] paid;

        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
        private static void Placing(Player __instance, Piece piece) => placing = __instance == Player.m_localPlayer ? piece : null;

        [HarmonyFinalizer, HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
        private static void Placed() => placing = null;

        [HarmonyTranspiler, HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
        private static IEnumerable<CodeInstruction> RecordOnCreation(IEnumerable<CodeInstruction> instructions) => Transpilers.CallInstead(instructions,
            instruction => instruction.operand is MethodInfo method && method.DeclaringType == typeof(UnityEngine.Object) && method.Name == "Instantiate" && method.ReturnType == typeof(GameObject) &&
                method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(GameObject), typeof(Vector3), typeof(Quaternion) }),
            AccessTools.Method(typeof(ConstructionPayment), nameof(Create)), 1, "Native construction creation moved.");

        // Records the cost before the placement callbacks run, so a piece that exists always knows what it cost.
        private static GameObject Create(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            var placed = UnityEngine.Object.Instantiate(prefab, position, rotation);
            var piece = placing; var player = Player.m_localPlayer;
            if (!piece || prefab != piece.gameObject || !player) return placed;
            placing = null;
            var station = player.GetCurrentCraftingStation(); bool upgrader = station && station.m_upgrader;
            paid = player.NoCostCheat() || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()) ? Array.Empty<Piece.Requirement>() : piece.m_resources;
            var refunds = paid.Where(row => row?.m_resItem != null && row.m_upgraderResource == upgrader && row.m_recover && row.GetAmount(0) > 0)
                .GroupBy(row => row.m_resItem.name).ToDictionary(group => group.Key, group => group.Sum(row => row.GetAmount(0)));
            var view = placed.GetComponent<ZNetView>();
            if (view && view.IsValid()) view.GetZDO().Set(PaidKey, JsonConvert.SerializeObject(refunds));
            return placed;
        }

        [HarmonyTranspiler, HarmonyPatch(typeof(Player), "UpdatePlacement")]
        private static IEnumerable<CodeInstruction> PayRecordedCost(IEnumerable<CodeInstruction> instructions) => Transpilers.CallInstead(instructions,
            instruction => instruction.Calls(AccessTools.Method(typeof(Player), nameof(Player.ConsumeResources))), AccessTools.Method(typeof(ConstructionPayment), nameof(Consume)),
            1, "Native construction consumption moved.");

        // The game's own consumption, which AzuCraftyBoxes extends to nearby chests, with the cost the piece recorded: the
        // game would otherwise take a no-cost builder's materials too.
        private static void Consume(Player player, Piece.Requirement[] requirements, int quality, int itemQuality, int multiplier)
        {
            var recorded = paid; paid = null;
            player.ConsumeResources(recorded ?? requirements, quality, itemQuality, multiplier);
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(Player), "UpdatePlacement")]
        private static void Finished() { placing = null; paid = null; }

        [HarmonyPrefix, HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
        private static void PaidRefund(Piece __instance, out Piece.Requirement[] __state)
        {
            __state = null; var view = __instance.GetComponent<ZNetView>();
            string saved = view && view.IsValid() ? view.GetZDO().GetString(PaidKey, "") : "";
            if (saved.Length == 0) return;
            var recorded = JsonConvert.DeserializeObject<Dictionary<string, int>>(saved);
            var resources = recorded.Select(row => new Piece.Requirement { m_resItem = ObjectDB.instance.GetItemPrefab(row.Key).GetComponent<ItemDrop>(), m_amount = row.Value, m_recover = true }).ToArray();
            __state = __instance.m_resources; __instance.m_resources = resources;
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
        private static void RestoreRequirements(Piece __instance, Piece.Requirement[] __state)
        { if (__state != null) __instance.m_resources = __state; }
    }
}
