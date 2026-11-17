using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // With [Smelting] Require boss defeats, flametal and bloodgold smelt only once the world has the key its setting names,
    // by default Yagluth's and the Queen's; the world's keys are read each time, so a defeat applies at once. Ore can still be
    // mined, carried and stored, and bars already made stay usable. A furnace takes no locked ore, whether added by hand,
    // from nearby chests or with pull all, and its owner refuses any that arrives. Ore queued before keeps its place and
    // partial progress: while it heads the queue the furnace burns no fuel, makes nothing and looks idle, and it carries
    // on by itself once the boss falls. Destroying the furnace still returns queued ore, and finished bars stay to collect.
    [HarmonyPatch]
    internal static class SmeltingProgression
    {
        // The bar each lock holds back, the setting naming the world key that releases it and the metal a player is told about.
        private static readonly Dictionary<string, (ConfigEntry<string> Key, string Metal)> Locks = new Dictionary<string, (ConfigEntry<string>, string)>
        {
            ["FlametalNew"] = (Plugin.FlametalWorldKey, "Flametal"),
            ["Gold"] = (Plugin.BloodgoldWorldKey, "Bloodgold"),
        };
        // The reason a player is told for each metal and key, named after the creature whose defeat sets the key.
        private static readonly Dictionary<(string Metal, string Key), string> Reasons = new Dictionary<(string, string), string>();

        private static string Reason(string metal, string key)
        {
            if (Reasons.TryGetValue((metal, key), out var reason)) return reason;
            var boss = ZNetScene.instance?.m_prefabs.Select(prefab => prefab ? prefab.GetComponent<Character>() : null)
                .FirstOrDefault(character => character && character.m_defeatSetGlobalKey == key);
            reason = boss ? $"{metal} requires defeating {Localization.instance.Localize(boss.m_name)}" : $"{metal} can't be smelted yet";
            if (ZNetScene.instance) Reasons[(metal, key)] = reason;
            return reason;
        }

        private static readonly Func<Smelter, string> QueuedOre = AccessTools.MethodDelegate<Func<Smelter, string>>(AccessTools.Method(typeof(Smelter), "GetQueuedOre"));

        // Smelter.GetItemConversion, which compares each conversion's ore by its prefab's name, read once per prefab.
        private static Smelter.ItemConversion Conversion(Smelter smelter, string ore)
        {
            foreach (var conversion in smelter.m_conversion)
                if (conversion.m_from == null || GameFields.PrefabName(conversion.m_from.gameObject) == ore) return conversion;
            return null;
        }
        // While a player's add-ore action runs: why the ore they carry cannot go in, shown instead of "no processable items".
        private static string carriedReason;

        // Why the conversion cannot run yet, or null when it can.
        private static string Locked(Smelter.ItemConversion conversion)
        {
            if (!Plugin.SmeltingRequiresBosses.Value || conversion?.m_to == null || !ZoneSystem.instance) return null;
            if (!Locks.TryGetValue(GameFields.PrefabName(conversion.m_to.gameObject), out var gate)) return null;
            string key = gate.Key.Value.Trim();
            return key.Length > 0 && !ZoneSystem.instance.GetGlobalKey(key) ? Reason(gate.Metal, key) : null;
        }

        internal static string Locked(Smelter smelter, string ore) => Locked(Conversion(smelter, ore));

        // The queue's head while it can be smelted. A locked head reads as an empty queue, so the step burns no fuel and
        // makes nothing, while the queue, its order, the progress on the head and the game's clock stay as they are.
        private static string ProcessableOre(Smelter smelter)
        {
            string ore = QueuedOre(smelter);
            return ore.Length > 0 && Plugin.SmeltingRequiresBosses.Value && Locked(smelter, ore) != null ? "" : ore;
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
