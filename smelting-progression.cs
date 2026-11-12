using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // With [Smelting] Require boss defeats, flametal smelts only once the world has defeated Yagluth and bloodgold only
    // once it has defeated the Queen; the world's keys are read each time, so a defeat applies at once. Ore can still be
    // mined, carried and stored, and bars already made stay usable. A furnace takes no locked ore, whether added by hand,
    // from nearby chests or with pull all, and its owner refuses any that arrives. Ore queued before keeps its place and
    // partial progress: while it heads the queue the furnace burns no fuel, makes nothing and looks idle, and it carries
    // on by itself once the boss falls. Destroying the furnace still returns queued ore, and finished bars stay to collect.
    [HarmonyPatch]
    internal static class SmeltingProgression
    {
        // The bar each lock holds back, the world key that releases it and the reason a player is told.
        private static readonly Dictionary<string, (string Key, string Reason)> Locks = new Dictionary<string, (string, string)>
        {
            ["FlametalNew"] = ("defeated_goblinking", "Flametal requires defeating Yagluth"),
            ["Gold"] = ("defeated_queen", "Bloodgold requires defeating the Queen"),
        };

        private static readonly Func<Smelter, string> QueuedOre = AccessTools.MethodDelegate<Func<Smelter, string>>(AccessTools.Method(typeof(Smelter), "GetQueuedOre"));
        private static readonly Func<Smelter, string, Smelter.ItemConversion> Conversion =
            AccessTools.MethodDelegate<Func<Smelter, string, Smelter.ItemConversion>>(AccessTools.Method(typeof(Smelter), "GetItemConversion"));
        // While a player's add-ore action runs: why the ore they carry cannot go in, shown instead of "no processable items".
        private static string carriedReason;

        // Why the conversion cannot run yet, or null when it can.
        private static string Locked(Smelter.ItemConversion conversion)
        {
            if (!Plugin.SmeltingRequiresBosses.Value || conversion?.m_to == null || !ZoneSystem.instance) return null;
            return Locks.TryGetValue(conversion.m_to.gameObject.name, out var gate) && !ZoneSystem.instance.GetGlobalKey(gate.Key) ? gate.Reason : null;
        }

        internal static string Locked(Smelter smelter, string ore) => Locked(Conversion(smelter, ore));

        // The queue's head while it can be smelted. A locked head reads as an empty queue, so the step burns no fuel and
        // makes nothing, while the queue, its order, the progress on the head and the game's clock stay as they are.
        private static string ProcessableOre(Smelter smelter)
        {
            string ore = QueuedOre(smelter);
            return ore.Length > 0 && Locked(smelter, ore) != null ? "" : ore;
        }

        [HarmonyPatch(typeof(Smelter), "UpdateSmelter"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> PauseLocked(IEnumerable<CodeInstruction> instructions)
        {
            var queued = AccessTools.Method(typeof(Smelter), "GetQueuedOre");
            var code = instructions.ToList();
            var reads = code.Where(instruction => instruction.Calls(queued)).ToList();
            // Valheim 1.0.17: the step loop reads the head, and the end of the update reads it to spill finished bars.
            if (reads.Count != 2) throw new InvalidOperationException("Valheim's smelter update changed; expected two queue reads.");
            foreach (var read in reads)
            {
                read.opcode = OpCodes.Call;
                read.operand = AccessTools.Method(typeof(SmeltingProgression), nameof(ProcessableOre));
            }
            return code;
        }

        // A furnace paused on locked ore shows no fire or animation.
        [HarmonyPatch(typeof(Smelter), nameof(Smelter.IsActive)), HarmonyPostfix]
        private static void Idle(Smelter __instance, ref bool __result)
        {
            if (__result && __instance.m_maxOre != 0 && ProcessableOre(__instance).Length == 0) __result = false;
        }

        // The owner adds only ore it may smelt.
        [HarmonyPatch(typeof(Smelter), "IsItemAllowed", typeof(string)), HarmonyPostfix]
        private static void Refuse(Smelter __instance, string itemName, ref bool __result)
        {
            if (__result && Locked(__instance, itemName) != null) __result = false;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.Message)), HarmonyPrefix]
        private static void ExplainNothingAdded(ref string msg)
        {
            if (carriedReason != null && msg == "$msg_noprocessableitems") msg = carriedReason;
        }

        // Before AzuCraftyBoxes' nearby-chest loading, the game's own choice and pull all, which all read the conversion
        // list, it holds only unlocked ore; the full list comes back however they end.
        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        private static class AddOre
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Smelter __instance, Humanoid user, ItemDrop.ItemData item, out List<Smelter.ItemConversion> __state)
            {
                __state = null;
                if (item != null)
                {
                    string reason = item.m_dropPrefab ? Locked(__instance, item.m_dropPrefab.name) : null;
                    if (reason == null) return true;
                    user.Message(MessageHud.MessageType.Center, reason);
                    return false;
                }
                var locked = __instance.m_conversion.Where(conversion => Locked(conversion) != null).ToList();
                if (locked.Count == 0) return true;
                __state = __instance.m_conversion;
                __instance.m_conversion = __state.Except(locked).ToList();
                carriedReason = locked.Where(conversion => conversion.m_from && user.GetInventory().HaveItem(conversion.m_from.m_itemData.m_shared.m_name))
                    .Select(Locked).FirstOrDefault();
                return true;
            }

            private static Exception Finalizer(Smelter __instance, List<Smelter.ItemConversion> __state, Exception __exception)
            {
                if (__state != null) __instance.m_conversion = __state;
                carriedReason = null;
                return __exception;
            }
        }
    }
}
