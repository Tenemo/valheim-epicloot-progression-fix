using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Repeat actual enchanted equipment, by default once, independently rerolling its enchantments. Keep all other drops native.
    // A death with no equipment gets one item from the creature's own equipment branch, at its normal star band.
    internal static class BountyLoot
    {
        private sealed class Reward { internal bool HasEquipment; }
        private static readonly int CorpseKey = (Plugin.Id + ".bounty-loot").GetStableHashCode();
        private static readonly MethodInfo PlanRoll = AccessTools.Method(typeof(LootRoller), "PlanRoll");
        private static readonly FieldInfo RollScaling = AccessTools.Field(PlanRoll.ReturnType, "Scaling");
        private static readonly MethodInfo ResolveReference = AccessTools.Method(typeof(LootRoller), "TryResolveTableReference");
        [ThreadStatic] private static Reward current;
        [ThreadStatic] private static bool bonus;
        [ThreadStatic] private static bool guaranteeing;
        private static ManualLogSource logger;

        internal static bool NeedsFallback => guaranteeing;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            logger = log;
            var self = typeof(BountyLoot);
            CreatureLootScope.Patch(harmony, self);
            harmony.Patch(AccessTools.Method(typeof(LootRoller), "SpawnNormalItem"),
                prefix: new HarmonyMethod(self, nameof(BeforeEquipment)), postfix: new HarmonyMethod(self, nameof(DoubleEquipment)));
        }

        private static bool MainTarget(CharacterDrop drop)
        {
            var character = drop ? drop.GetComponent<Character>() : null;
            var view = character && !character.IsPlayer() && !character.IsTamed() ? character.GetComponent<ZNetView>() : null;
            var data = view && view.IsValid() ? view.GetZDO() : null;
            return data != null && BountyCreature.IsMainTarget(data);
        }

        private static void BeginDeath(CharacterDrop __0, out Reward __state)
        {
            __state = current;
            current = MainTarget(__0) ? new Reward() : null;
        }

        private static void RememberCorpse(Ragdoll __0, CharacterDrop __1)
        {
            var view = __0 ? __0.GetComponent<ZNetView>() : null;
            if (view && view.IsValid()) view.GetZDO().Set(CorpseKey, MainTarget(__1));
        }

        private static void BeginCorpse(Ragdoll __0, out Reward __state)
        {
            __state = current;
            var view = __0 ? __0.GetComponent<ZNetView>() : null;
            current = view && view.IsValid() && view.GetZDO().GetBool(CorpseKey) ? new Reward() : null;
        }

        private static void EndDeath(Reward __state) => current = __state;

        private static bool Enchanted(GameObject item) => item && item.TryGetComponent<ItemDrop>(out var drop) &&
            global::EpicLoot.EpicLoot.IsAllowedMagicItemType(drop.m_itemData) && drop.m_itemData.IsMagic();

        private static void BeforeEquipment(object output, out int __state) => __state = LootOutput.Count(output);

        private static void DoubleEquipment(LootDrop lootDrop, string objectName, Vector3 dropPoint, float luckFactor,
            object output, int __state)
        {
            if (current == null || bonus || !LootOutput.Items(output).Skip(__state).Any(item => global::EpicLoot.EpicLoot.IsAllowedMagicItemType(item) && item.IsMagic())) return;
            current.HasEquipment = true;
            if (guaranteeing) return;
            bonus = true;
            try
            {
                for (int copy = 0; copy < Plugin.BountyExtraEquipmentCopies.Value; copy++)
                    LootOutput.Spawn(lootDrop, objectName, dropPoint, luckFactor, output);
            }
            finally { bonus = false; }
        }

        // The pack's equipment branches contain equipment sets or a reference to a biome/star equipment table;
        // the separate elite shard branches do not. Keep the original branch for EpicLoot's weighted resolver.
        internal static bool EquipmentBranch(LootDrop source) => EquipmentBranch(source, null);

        // A wildcard reference such as Tier9Mob.* resolves at the rolling level, as EpicLoot's resolver does; without a
        // rolling level, any of the referenced table's rows counts.
        private static bool EquipmentBranch(LootDrop source, LootScalingContext rolling)
        {
            var prefab = ObjectDB.instance.GetItemPrefab(source.Item);
            if (prefab && prefab.TryGetComponent<ItemDrop>(out var drop))
                return global::EpicLoot.EpicLoot.IsAllowedMagicItemType(drop.m_itemData);
            if (LootRoller.ItemSets.TryGetValue(source.Item, out var set)) return set.Loot.Any(entry => EquipmentBranch(entry, rolling));
            if (LootRoller.LootTables.TryGetValue(source.Item, out var tables))
                return tables.SelectMany(table => LootRoller.GetLootForLevel(table, 1)).Any(entry => EquipmentBranch(entry, rolling));
            var reference = source.Item.Split('.');
            if (reference.Length != 2) return false;
            if (reference[1] != "*")
                return int.TryParse(reference[1], out int level) &&
                    LootRoller.GetLootTable(reference[0]).SelectMany(table => LootRoller.GetLootForLevel(table, level)).Any(entry => EquipmentBranch(entry, rolling));
            if (rolling == null)
                return LootRoller.GetLootTable(reference[0]).SelectMany(table => table.LeveledLoot).SelectMany(row => row.Loot ?? Array.Empty<LootDrop>()).Any(EquipmentBranch);
            var arguments = new object[] { source.Item, rolling, null, null };
            if (!(bool)ResolveReference.Invoke(null, arguments)) return false;
            var referenced = (LootScalingContext)arguments[3];
            return ((LootDrop[])arguments[2]).Any(entry => LootScalingMath.ScaledWeight(entry, referenced.LootSteps) > 0f && EquipmentBranch(entry, referenced));
        }

        internal static void EnsureEquipment(List<LootTable> tables, int level, string creatureName, Vector3 dropPoint,
            List<GameObject> results)
        {
            if (current == null || current.HasEquipment) return;
            float luck = LootRoller.GetLuckFactor(dropPoint);
            var sources = tables.SelectMany(table =>
            {
                var scaling = (LootScalingContext)RollScaling.GetValue(PlanRoll.Invoke(null, new object[] { table, level, creatureName }));
                return LootRoller.GetLootForLevel(table, level).Where(drop => EquipmentBranch(drop, scaling)).Select(drop => (Drop: drop, Scaling: scaling));
            }).ToList();
            // A configuration gap leaves this bounty with its native drops; it must not abort the creature's other loot.
            if (sources.Count == 0)
            {
                logger.LogError($"Bounty creature {creatureName} has no equipment loot branch; its guaranteed enchanted item was skipped.");
                return;
            }
            var pool = new WeightedRandomCollection<(LootDrop Drop, LootScalingContext Scaling)>();
            pool.Setup(sources, source => LootScalingMath.ScaledWeight(source.Drop, source.Scaling.LootSteps));
            var selected = pool.Roll();
            var equipment = LootRoller.ResolveLootDrop(selected.Drop, luck, false, selected.Scaling);
            guaranteeing = true;
            try
            {
                LootOutput.Spawn(equipment, creatureName, dropPoint, luck, LootOutput.ForObjects(results));
                if (!current.HasEquipment)
                    logger.LogError($"Bounty creature {creatureName} did not produce its guaranteed enchanted item.");
            }
            finally { guaranteeing = false; }
        }
    }
}
