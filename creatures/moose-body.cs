using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A moose's body is two stacked capsules with a groove between them. A large one swims with the lower capsule under shore ice
    // and the upper one above it, and the ice's edge wedges in that groove for good. A convex hull over both capsules closes the
    // groove without reaching past them, so the moose meets the ice as one solid front.
    [HarmonyPatch]
    internal static class MooseBody
    {
        private const string Feature = "Moose shoreline collision", Hull = "Continuous body";
        private const int Sides = 8;
        private static readonly string[] Prefabs = { "Moose", "Moose_spiritcaller" };

        internal static bool IsContinuousBody(Collider collider) => collider is MeshCollider && collider.name == Hull && collider.transform.parent;

        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix]
        private static void CloseGrooves(ZNetScene __instance)
        {
            // The dedicated server's moose has only the upper capsule, so it has no groove.
            if (ZNet.instance && ZNet.instance.IsDedicated()) return;
            foreach (string name in Prefabs)
            {
                var prefab = __instance.GetPrefab(name);
                if (prefab && prefab.transform.Find(Hull)) continue;
                var capsules = prefab ? prefab.GetComponentsInChildren<CapsuleCollider>(true).Where(capsule => capsule.enabled && !capsule.isTrigger).ToArray() : null;
                if (capsules?.Length != 2) { DisabledFeatures.Set(Feature, name + " no longer has two body capsules"); continue; }
                var holder = new GameObject(Hull) { layer = prefab.layer };
                holder.SetActive(false);
                holder.transform.SetParent(prefab.transform, false);
                holder.AddComponent<MeshCollider>().sharedMesh = Build(name, prefab.transform, capsules);
                holder.GetComponent<MeshCollider>().convex = true;
                holder.AddComponent<SharedFriction>();
                holder.SetActive(true);
            }
        }

        private static Mesh Build(string name, Transform root, CapsuleCollider[] capsules)
        {
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            foreach (var capsule in capsules)
            {
                var axis = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
                var first = capsule.direction == 0 ? Vector3.up : Vector3.right; var second = Vector3.Cross(axis, first);
                float radius = capsule.radius, segment = Mathf.Max(0f, capsule.height * .5f - radius);
                var along = new[] { -segment - radius, -segment - radius * .866f, -segment - radius * .5f, -segment, segment, segment + radius * .5f, segment + radius * .866f, segment + radius };
                var around = new[] { 0f, .5f, .866f, 1f, 1f, .866f, .5f, 0f };
                int start = vertices.Count;
                for (int ring = 0; ring < along.Length; ring++)
                    for (int side = 0; side < Sides; side++)
                    {
                        float angle = side * Mathf.PI * 2f / Sides;
                        var point = capsule.center + axis * along[ring] + (first * Mathf.Cos(angle) + second * Mathf.Sin(angle)) * around[ring] * radius;
                        vertices.Add(root.InverseTransformPoint(capsule.transform.TransformPoint(point)));
                    }
                for (int ring = 0; ring < along.Length - 1; ring++)
                    for (int side = 0; side < Sides; side++)
                    {
                        int near = start + ring * Sides + side, next = start + ring * Sides + (side + 1) % Sides;
                        triangles.AddRange(new[] { near, next, near + Sides, next, next + Sides, near + Sides });
                    }
            }
            var mesh = new Mesh { name = name + " continuous body" };
            mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
            Object.DontDestroyOnLoad(mesh);
            return mesh;
        }

        // The game changes the friction of the upper capsule's own material as the moose swims, walks and falls.
        private sealed class SharedFriction : MonoBehaviour
        {
            private void Awake() => GetComponent<Collider>().sharedMaterial = transform.parent.GetComponent<CapsuleCollider>().material;
        }
    }
}
