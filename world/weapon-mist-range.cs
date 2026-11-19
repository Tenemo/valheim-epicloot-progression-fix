using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class WeaponMistRange
    {
        private static ConfigEntry<float> baseRange;
        private static ConfigEntry<float> rangePerLevel;
        // The range each held model's force field was given, kept while the model exists.
        private static readonly ConditionalWeakTable<Demister, StrongBox<float>> Ranges = new ConditionalWeakTable<Demister, StrongBox<float>>();
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
            harmony.Patch(AccessTools.Method(typeof(Demister), "OnEnable"),
                postfix: new HarmonyMethod(typeof(WeaponMistRange), nameof(KeepRange)) { priority = Priority.Low });
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
            // A model attached to a hidden hand, as in the Jotun Witch form, wakes only when the hand shows again, so its
            // force field isn't stored yet. Throwing here would leave the model in the hand after the next equip.
            float range = baseRange.Value + rangePerLevel.Value * (Mathf.Max(1, quality) - 1);
            foreach (var demister in __result.GetComponentsInChildren<Demister>())
            {
                var field = demister.m_forceField ? demister.m_forceField : demister.GetComponent<ParticleSystemForceField>();
                if (!field) continue;
                field.endRange = range;
                Ranges.Remove(demister);
                Ranges.Add(demister, new StrongBox<float>(range));
            }
        }

        // Better Wisps gives every force field that turns on the Wisplight's range. A held model's field turns on again
        // when its hidden hand shows, so it gets its weapon's range back afterwards.
        private static void KeepRange(Demister __instance)
        {
            if (Ranges.TryGetValue(__instance, out var range) && __instance.m_forceField) __instance.m_forceField.endRange = range.Value;
        }
    }
}
