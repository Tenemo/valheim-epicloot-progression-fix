using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // StarLevelSystem can hatch an egg at its quality, and can have a starred parent lay a larger stack of eggs instead of
    // one egg of its level. Its stacks always have quality 1, and the hatch level never arrived: the hatchling's own spawn
    // setup is already queued when the egg asks for its level, and the queue drops the second request, so the hatchling
    // rolled a level as a wild spawn does. With hatching by quality on, a stack keeps the level of the parent that laid it,
    // as a single egg does, and the hatchling takes that level, as live-born young take their parent's.
    internal static class EggStackLevels
    {
        private static readonly AccessTools.FieldRef<Procreation, Character> Parent = AccessTools.FieldRefAccess<Procreation, Character>("m_character");
        private static ConfigEntry<bool> stacks, hatchAtQuality;
        private static System.Reflection.MethodInfo storeLevel;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var config = StarLevelSystem.Assembly.GetType("StarLevelSystem.common.ValConfig");
            stacks = AccessTools.Field(config, "LootEggsDropIncreaseStacks")?.GetValue(null) as ConfigEntry<bool>;
            hatchAtQuality = AccessTools.Field(config, "EggLevelDeterminedByItemQuality")?.GetValue(null) as ConfigEntry<bool>;
            var breeding = StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.LevelSystem.LevelPatches+SetChildLevel");
            var layEgg = AccessTools.Method(breeding, "SetupEggItem", new[] { typeof(ItemDrop), typeof(Procreation) });
            var hatch = AccessTools.Method(breeding, "ControlEggSpawnLevelInheritance", new[] { typeof(Character), typeof(EggGrow) });
            var modifierType = StarLevelSystem.Assembly.GetType("StarLevelSystem.common.DataObjects+ModifierType");
            storeLevel = modifierType == null ? null : AccessTools.Method(StarLevelSystem.CompositeLazyCache, "GetAndSetLocalCache",
                new[] { typeof(Character), typeof(int), typeof(Dictionary<,>).MakeGenericType(typeof(string), modifierType), typeof(List<string>), typeof(bool) });
            if (stacks == null || hatchAtQuality == null || layEgg == null || hatch == null || storeLevel == null)
                throw new InvalidOperationException("StarLevelSystem's egg stacks or egg hatching levels changed.");
            harmony.Patch(layEgg, postfix: new HarmonyMethod(typeof(EggStackLevels), nameof(KeepParentLevel)));
            harmony.Patch(hatch, prefix: new HarmonyMethod(typeof(EggStackLevels), nameof(HatchAtEggLevel)));
        }

        // The level StarLevelSystem gives a single egg, without its chance of an extra level, which only single eggs roll.
        private static void KeepParentLevel(ItemDrop __0, Procreation __1)
        {
            if (!stacks.Value || !hatchAtQuality.Value || !__0 || !__1) return;
            var parent = Parent(__1);
            __0.SetQuality(Mathf.Max(__1.m_minOffspringLevel, parent ? parent.GetLevel() : __1.m_minOffspringLevel));
        }

        // Stores the level as StarLevelSystem stores a grown-up young's, so the queued spawn setup applies it instead of a roll.
        private static void HatchAtEggLevel(Character __0, EggGrow __1)
        {
            if (!hatchAtQuality.Value || !__0 || !__1) return;
            var egg = __1.GetComponent<ItemDrop>();
            if (egg) storeLevel.Invoke(null, new object[] { __0, egg.m_itemData.m_quality, null, null, true });
        }
    }
}
