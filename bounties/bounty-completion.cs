using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EpicLoot.Adventure;
using EpicLoot.Adventure.Feature;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Keep the native owner, contract and main-target checks, completion notification, pin removal and save.
    // Only the minion-count requirement changes; surviving escorts stay in the world.
    [HarmonyPatch(typeof(BountiesAdventureFeature), "OnBountyTargetSlain",
        new[] { typeof(AdventureSaveData), typeof(string), typeof(string), typeof(bool) })]
    internal static class BountyCompletion
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var slain = AccessTools.Field(typeof(BountyInfo), nameof(BountyInfo.Slain));
            int targetCheck = code.FindIndex(instruction => instruction.LoadsField(slain));
            int sum = code.FindIndex(instruction => instruction.operand is MethodInfo method &&
                method.DeclaringType == typeof(Enumerable) && method.Name == nameof(Enumerable.Sum) &&
                method.ReturnType == typeof(int) && method.IsGenericMethod &&
                method.GetGenericArguments().SequenceEqual(new[] { typeof(BountyTargetInfo) }));
            if (targetCheck < 0 || sum <= targetCheck + 2 || sum + 3 >= code.Count ||
                !(code[targetCheck + 1].opcode == OpCodes.Brfalse || code[targetCheck + 1].opcode == OpCodes.Brfalse_S) ||
                code[sum + 1].opcode != OpCodes.Ldc_I4_0 || code[sum + 2].opcode != OpCodes.Ceq ||
                !(code[sum + 3].opcode == OpCodes.Br || code[sum + 3].opcode == OpCodes.Br_S))
                throw new InvalidOperationException("EpicLoot's bounty completion condition changed.");
            int start = targetCheck + 2, count = sum + 3 - start;
            // With the main target slain, the remaining part of the condition is true. Its false branch stays false.
            var complete = new CodeInstruction(OpCodes.Ldc_I4_1);
            foreach (var instruction in code.GetRange(start, count))
            {
                complete.labels.AddRange(instruction.labels);
                complete.blocks.AddRange(instruction.blocks);
            }
            code.RemoveRange(start, count);
            code.Insert(start, complete);
            return code;
        }

        private static void Prefix(AdventureSaveData saveData, string bountyID, out bool __state) =>
            __state = saveData?.GetBountyInfoByID(bountyID)?.State == BountyState.InProgress;

        private static void Postfix(AdventureSaveData saveData, string bountyID, bool __state)
        {
            if (__state && saveData.GetBountyInfoByID(bountyID)?.State == BountyState.Complete)
                AndvaranautTracking.RefreshTarget();
        }
    }

    // Older contracts already record whether the main target died. Finish those on the owner's next current-world
    // lookup, keeping the existing save format and the remaining minion counts. Claimed and abandoned records stay put.
    [HarmonyPatch(typeof(PlayerExtensions_Adventure), nameof(PlayerExtensions_Adventure.GetAdventureSaveData))]
    internal static class ExistingBountyCompletion
    {
        private static readonly Action<BountyInfo> RemovePin = AccessTools.MethodDelegate<Action<BountyInfo>>(
            AccessTools.Method(typeof(BountiesAdventureFeature), "RemoveMinimapPin"));

        private static void Postfix(Player player, AdventureSaveData __result)
        {
            if (player != Player.m_localPlayer || ZNet.instance == null || ZNet.instance.GetWorldUID() == 0) return;
            long playerId = player.GetPlayerID();
            bool changed = false;
            foreach (var bounty in __result.Bounties)
            {
                if (bounty.PlayerID != playerId || bounty.State != BountyState.InProgress || !bounty.Slain) continue;
                bounty.State = BountyState.Complete;
                RemovePin(bounty);
                changed = true;
            }
            if (!changed) return;
            player.SaveAdventureSaveData();
            // All records are updated before this lookup, so refreshing the ring cannot repeat the migration.
            AndvaranautTracking.RefreshTarget();
        }
    }

    [HarmonyPatch(typeof(BountyListElement), "GetTooltip")]
    internal static class BountyObjectiveText
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int changes = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldstr && (string)instruction.operand == "$mod_epicloot_bounties_tooltip_slaymultiple")
                {
                    instruction.operand = "$mod_epicloot_bounties_tooltip_slay";
                    changes++;
                }
                yield return instruction;
            }
            if (changes != 1) throw new InvalidOperationException("EpicLoot's bounty objective text changed.");
        }
    }
}
