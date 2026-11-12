using System;
using System.Collections.Generic;
using System.Reflection;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // MineRock5 uses the section center for effects and loot, even when that center is still buried. Keep its
    // damage, effects and SLS level/loot rolls, but give item creation the exposed side of the pickaxe strike.
    [HarmonyPatch]
    internal static class MiningDropPosition
    {
        internal static Vector3? StrikePosition;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MineRock5), "DamageArea");
            yield return AccessTools.Method(typeof(MineRock), "RPC_Hit");
        }

        private static void Prefix(HitData hit, out Vector3? __state)
        {
            __state = StrikePosition;
            StrikePosition = null;
            if (hit?.GetAttacker() is not Player player || hit.m_damage.m_pickaxe <= 0f) return;
            Vector3 direction = hit.m_dir.sqrMagnitude > 0f ? hit.m_dir.normalized : (hit.m_point - player.GetCenterPoint()).normalized;
            StrikePosition = hit.m_point - direction * 0.5f + Vector3.up * 0.2f;
        }

        // EpicLoot creates its bonus in a postfix. Restore only after all postfixes, including on failure or a
        // nested hit; unsupported sections collapse outside the player strike's scope and keep their own drops.
        private static void Finalizer(Vector3? __state) => StrikePosition = __state;
    }

    [HarmonyPatch]
    internal static class MiningScaledDropPosition
    {
        private static bool Prepare() => StarLevelSystem.Installed;

        private static MethodBase TargetMethod() => AccessTools.Method(
            StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.Loot.LootPerformanceChanges"), "DropItemsPreferAsync")
            ?? throw new InvalidOperationException("StarLevelSystem's item drop dispatcher changed.");

        private static void Prefix(ref Vector3 __0)
        {
            // The coroutine copies this position before it yields; later batches cannot inherit another strike.
            // SLS has already calculated the rock level and quantities using the original section position.
            if (MiningDropPosition.StrikePosition is Vector3 position) __0 = position;
        }
    }

    [HarmonyPatch(typeof(IncreaseDrop), nameof(IncreaseDrop.TryDropExtraItems))]
    internal static class MiningBonusDropPosition
    {
        private static void Prefix(IncreaseDrop __instance, ref Vector3 objPosition)
        {
            if (__instance is IncreaseMiningDrop && MiningDropPosition.StrikePosition is Vector3 position)
                objPosition = position;
        }
    }
}
