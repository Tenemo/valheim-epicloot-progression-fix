using System;
using System.Collections.Generic;
using System.Linq;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The boss count tables guarantee a roll, but EpicLoot can turn every roll into materials, shards or bundles.
    // Reserve the first equipment roll for an enchanted item. Lucky Loot runs inside the same death callback, so
    // it does not receive another guaranteed item for each bonus roll. Native CharacterDrop loot is untouched.
    internal static class BossLoot
    {
        private sealed class Reward { internal bool NeedsEquipment = true; }
        [ThreadStatic] private static Reward current;
        [ThreadStatic] private static bool guaranteeing;

        internal static bool NeedsFallback => current != null && current.NeedsEquipment && guaranteeing;

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(BossLoot);
            CreatureLootScope.Patch(harmony, self);
            harmony.Patch(AccessTools.Method(typeof(LootRoller), "SelectDropType"),
                prefix: new HarmonyMethod(self, nameof(ReserveEquipment)));
            harmony.Patch(AccessTools.Method(typeof(LootRoller), "SpawnNormalItem"),
                prefix: new HarmonyMethod(self, nameof(BeginEquipment)), postfix: new HarmonyMethod(self, nameof(EquipmentCreated)),
                finalizer: new HarmonyMethod(self, nameof(EndEquipment)));
        }

        private static Reward ForBoss(string name)
        {
            var prefab = string.IsNullOrEmpty(name) ? null : ZNetScene.instance.GetPrefab(name)?.GetComponent<Character>();
            // BossOrder excludes transition forms and hive controllers; bounties do not change their prefab's flag.
            return prefab && prefab.IsBoss() && prefab.BossOrder() > 0 ? new Reward() : null;
        }

        private static void BeginDeath(CharacterDrop __0, out Reward __state)
        {
            __state = current;
            var character = __0 ? __0.GetComponent<Character>() : null;
            current = character && !character.IsTamed() ? ForBoss(global::EpicLoot.EpicLoot.GetCharacterCleanName(character)) : null;
        }

        private static void BeginCorpse(Ragdoll __0, out Reward __state)
        {
            __state = current;
            var view = __0 ? __0.GetComponent<ZNetView>() : null;
            current = view && view.IsValid() ? ForBoss(view.GetZDO().GetString("characterName")) : null;
        }

        private static void EndDeath(Reward __state) => current = __state;

        private static bool Equipment(LootDrop drop)
        {
            var item = drop == null ? null : ObjectDB.instance.GetItemPrefab(drop.Item)?.GetComponent<ItemDrop>();
            return item && global::EpicLoot.EpicLoot.IsAllowedMagicItemType(item.m_itemData);
        }

        private static bool ReserveEquipment(LootDrop lootDrop, bool __1, ref LootDropType __result)
        {
            if (current == null || !current.NeedsEquipment || __1 || !Equipment(lootDrop)) return true;
            __result = LootDropType.Item;
            return false;
        }

        private static void BeginEquipment(LootDrop lootDrop, out bool __state)
        {
            __state = guaranteeing;
            guaranteeing = current != null && current.NeedsEquipment && Equipment(lootDrop);
        }

        private static void EquipmentCreated(object output)
        {
            if (!NeedsFallback) return;
            if (LootOutput.Items(output).Any(item => global::EpicLoot.EpicLoot.IsAllowedMagicItemType(item) && item.IsMagic()))
                current.NeedsEquipment = false;
        }

        private static void EndEquipment(bool __state) => guaranteeing = __state;
    }
}
