using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class WeaponMistRange
    {
        private static ConfigEntry<float> baseRange;
        private static ConfigEntry<float> rangePerLevel;
        private static readonly HashSet<int> AddedWeapons = new HashSet<int>
        {
            "KnifeSkollAndHati".GetStableHashCode(), "FW_KnifeSkollAndHati".GetStableHashCode(),
            "SP_KnifeSkollAndHati".GetStableHashCode(), "BattleaxeSkullSplittur".GetStableHashCode(),
            "StaffFireball".GetStableHashCode(), "StaffIceShards".GetStableHashCode(),
            "StaffShield".GetStableHashCode(), "StaffSkeleton".GetStableHashCode(),
            "GrapplingHook".GetStableHashCode()
        };

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.BetterWisps, out var plugin)) return;
            if (!plugin.Instance.Config.TryGetEntry("General", "Base Range", out baseRange) ||
                !plugin.Instance.Config.TryGetEntry("General", "Increased Range Per Level", out rangePerLevel))
                throw new InvalidOperationException("Better Wisps' range settings changed.");
            harmony.Patch(AccessTools.Method(typeof(VisEquipment), "AttachItem"),
                postfix: new HarmonyMethod(typeof(WeaponMistRange), nameof(SetRange)));
        }

        // Attachment has the weapon's replicated quality and runs after Better Wisps' OnEnable hook.
        // Only equipped visuals are changed; the Wisplight and placed torches keep their own range.
        private static void SetRange(GameObject __result, int itemHash, bool enableEquipEffects, int quality)
        {
            if (!__result || !enableEquipEffects) return;
            // Skoll and Hati's selected model omits its native effect. The other additions have none.
            // Copy only Mistwalker's force field onto the held visual; saved items and stowed models stay native.
            if (AddedWeapons.Contains(itemHash) && !__result.GetComponentInChildren<Demister>())
                UnityEngine.Object.Instantiate(ObjectDB.instance.GetItemPrefab("SwordMistwalker")
                    .GetComponentInChildren<Demister>(true).gameObject, __result.transform, false);
            foreach (var demister in __result.GetComponentsInChildren<Demister>())
                demister.m_forceField.endRange = baseRange.Value + rangePerLevel.Value * (Mathf.Max(1, quality) - 1);
        }
    }
}
