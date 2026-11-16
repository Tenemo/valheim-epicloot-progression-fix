using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A cart or ship that breaks or is taken apart packs its hold into cargo crates, which can end up sunk in the terrain.
    // The game raises a floating object only once its center is more than 1 m under the terrain, so a crate sunk less deep,
    // with its top under the surface, stayed out of sight and reach for good. Its owner's regular terrain check, every 30 s,
    // now also raises a crate that is more than half under the terrain onto the surface.
    [HarmonyPatch(typeof(Floating), "TerrainCheck")]
    internal static class BuriedCargoCrates
    {
        private static void Postfix(Floating __instance, ZNetView ___m_nview)
        {
            if (!___m_nview.IsValid() || !___m_nview.IsOwner() || Utils.GetPrefabName(__instance.gameObject) != "CargoCrate") return;
            var colliders = __instance.GetComponentsInChildren<Collider>().Where(collider => collider.enabled && !collider.isTrigger).ToList();
            if (colliders.Count == 0) return;
            Physics.SyncTransforms();
            var box = colliders.Select(collider => collider.bounds).Aggregate((all, next) => { all.Encapsulate(next); return all; });
            float ground = ZoneSystem.instance.GetGroundHeight(box.center);
            if (box.center.y >= ground) return;
            var position = __instance.transform.position;
            position.y += ground - box.min.y + .05f;
            __instance.transform.position = position;
            var body = __instance.GetComponent<Rigidbody>();
            if (body != null) { body.position = position; body.linearVelocity = Vector3.zero; }
            Physics.SyncTransforms();
        }
    }
}
