using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Aoe), "OnHit")]
    internal static class TamedCropTrampling
    {

        private static bool Prefix(Aoe __instance, Collider collider, ref bool __result)
        {
            var owner = GameFields.AoeOwner(__instance);
            if (!owner || !owner.IsTamed()) return true;
            var movement = owner.GetComponent<MovementDamage>();
            if (!movement || movement.m_runDamageObject != __instance.gameObject) return true;
            if (!Protected(Projectile.FindHitObject(collider))) return true;
            __result = false;
            return false;
        }

        // A farm crop or planting that tames must not trample: growing plants that do not become trees, pickables
        // with a creator and cultivator pieces, plus mature crops on cultivated ground from before creators were kept.
        internal static bool Protected(GameObject target)
        {
            if (!target) return false;
            var destructible = target.GetComponent<IDestructible>();
            if (destructible != null && (destructible.GetDestructibleType() & DestructibleType.Tree) != 0) return false;
            var plant = target.GetComponent<Plant>();
            var pickable = target.GetComponent<Pickable>();
            var piece = target.GetComponent<Piece>();
            if (!plant && !pickable && (!piece || !CultivatorPlant(target))) return false;
            if (plant)
                foreach (var grown in plant.m_grownPrefabs)
                    if (grown.GetComponent<TreeBase>()) return false;
            var view = target.GetComponent<ZNetView>();
            long creator = piece ? piece.GetCreator() : view && view.IsValid() ? view.GetZDO().GetLong(ZDOVars.s_creator) : 0L;
            // Older mature crops lost their creator on growth. Cultivated ground distinguishes the farm from wild
            // seed flowers and mushrooms, which share crop prefabs. Placed pickables retain their native creator.
            if (creator == 0L)
            {
                if (!pickable || !pickable.m_harvestable) return false;
                var ground = Heightmap.FindHeightmap(target.transform.position);
                if (!ground || !ground.IsCultivated(target.transform.position)) return false;
            }
            return true;
        }

        private static ObjectDB cultivatorSource;
        private static readonly HashSet<string> CultivatorPieces = new HashSet<string>();

        // The cultivator's pieces, read once per ObjectDB; without a cultivator nothing counts as planted with it.
        private static bool CultivatorPlant(GameObject target)
        {
            if (cultivatorSource != ObjectDB.instance)
            {
                cultivatorSource = ObjectDB.instance;
                CultivatorPieces.Clear();
                var cultivator = cultivatorSource ? cultivatorSource.GetItemPrefab("Cultivator") : null;
                var tool = cultivator ? cultivator.GetComponent<ItemDrop>() : null;
                var pieces = tool ? tool.m_itemData.m_shared.m_buildPieces : null;
                if (pieces) foreach (var prefab in pieces.m_pieces) if (prefab) CultivatorPieces.Add(prefab.name);
            }
            return CultivatorPieces.Contains(Utils.GetPrefabName(target));
        }
    }

    [HarmonyPatch(typeof(Plant), "Grow")]
    internal static class GrownPlantCreator
    {
        private static void Prefix(Plant __instance, out long __state)
        {
            var piece = __instance.GetComponent<Piece>();
            __state = piece ? piece.GetCreator() : 0L;
        }

        private static void Postfix(GameObject __result, long __state)
        {
            if (!__result || __state == 0L || __result.GetComponent<TreeBase>()) return;
            var view = __result.GetComponent<ZNetView>();
            if (view && view.IsValid()) view.GetZDO().Set(ZDOVars.s_creator, __state);
        }
    }
}
