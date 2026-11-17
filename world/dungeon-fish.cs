using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class DungeonFish
    {
        internal const string OriginKey = "epicloot_progression_fish_origin";
        private const int Natural = 1;
        private const int Dropped = 2;

        internal static bool IsFish(ZDO data) => ZNetScene.instance.GetPrefab(data.GetPrefab())?.GetComponent<Fish>();
        internal static bool BelongsTo(ZDO data, Vector2s zone) =>
            DungeonInteriors.Inside(data.GetVec3(ZDOVars.s_spawnPoint, Vector3.zero), zone) && IsFish(data);
        internal static bool Preserve(ZDO data) => data.GetPosition().y <= DungeonInteriors.InteriorHeight ||
            data.GetInt(OriginKey) != Natural || data.GetLong("creator") != 0;
        internal static bool Ambiguous(ZDO data) => data.GetInt(OriginKey) == 0;
        internal static bool Supplies(ZDO data, HashSet<int> species)
        {
            if (species.Count == 0) return false;
            var spawner = ZNetScene.instance.GetPrefab(data.GetPrefab())?.GetComponent<CreatureSpawner>();
            return spawner && spawner.m_creaturePrefab && species.Contains(spawner.m_creaturePrefab.name.GetStableHashCode());
        }

        [HarmonyPatch(typeof(ZNetView), "Awake")]
        private static class Creation
        {
            private static void Prefix(out bool __state) => __state = !ZNetView.m_useInitZDO && ZNetView.m_initZDO == null;
            private static void Postfix(ZNetView __instance, bool __state)
            {
                if (!__state || !__instance.IsValid() || __instance.transform.position.y <= DungeonInteriors.InteriorHeight || !__instance.GetComponent<Fish>()) return;
                var data = __instance.GetZDO();
                data.Set(OriginKey, Natural);
                data.Set(ZDOVars.s_spawnPoint, __instance.transform.position);
            }
        }

        [HarmonyPatch(typeof(Fish), "onDrop")]
        private static class PlayerDrop
        {
            private static void Postfix(Fish __instance)
            {
                var view = __instance.GetComponent<ZNetView>();
                if (view && view.IsValid() && view.IsOwner()) view.GetZDO().Set(OriginKey, Dropped);
            }
        }
    }
}
