using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The same physical body defines navigation clearance and placement. Attack triggers, another body's colliders,
    // and layers which cannot collide with the environment are not movement obstacles.
    internal static class CreatureClearance
    {
        internal struct Size
        {
            internal float Radius, HalfWidth, Bottom, Top;
            internal Bounds Bounds;
            internal bool Valid;
            internal float Height => Top - Bottom;
        }

        private sealed class Body
        {
            internal readonly List<Collider> Colliders = new List<Collider>();
            internal float RefreshAt;
        }
        private sealed class Vertices
        {
            internal Vector3[] Points;
        }

        private static readonly ConditionalWeakTable<Character, Body> Bodies = new ConditionalWeakTable<Character, Body>();
        private static readonly ConditionalWeakTable<Mesh, Vertices> Meshes = new ConditionalWeakTable<Mesh, Vertices>();
        private static readonly string[] EnvironmentLayers = { "terrain", "Default", "static_solid", "piece", "piece_nonsolid", "vehicle" };

        internal static List<Collider> Colliders(Character character, bool refresh = false)
        {
            var body = Bodies.GetOrCreateValue(character);
            if (refresh || Time.time >= body.RefreshAt)
            {
                body.RefreshAt = Time.time + .25f;
                character.GetComponentsInChildren(true, body.Colliders);
            }
            return body.Colliders;
        }

        internal static (bool Allowed, bool Overrides) LayerContact(Collider collider, Rigidbody body, int otherLayer, bool defaultContact)
        {
            int included = collider.includeLayers.value | (body ? body.includeLayers.value : 0);
            int excluded = collider.excludeLayers.value | (body ? body.excludeLayers.value : 0);
            int layer = 1 << otherLayer;
            return ((defaultContact || (included & layer) != 0) && (excluded & layer) == 0, (included | excluded) != 0);
        }

        internal static bool CanContact(Collider first, Collider second)
        {
            if (Physics.GetIgnoreCollision(first, second)) return false;
            bool native = !Physics.GetIgnoreLayerCollision(first.gameObject.layer, second.gameObject.layer);
            var firstDecision = LayerContact(first, first.attachedRigidbody, second.gameObject.layer, native);
            var secondDecision = LayerContact(second, second.attachedRigidbody, first.gameObject.layer, native);
            if (firstDecision.Allowed == secondDecision.Allowed) return firstDecision.Allowed;
            if (firstDecision.Overrides != secondDecision.Overrides) return firstDecision.Overrides ? firstDecision.Allowed : secondDecision.Allowed;
            if (first.layerOverridePriority == second.layerOverridePriority) return false;
            return first.layerOverridePriority > second.layerOverridePriority ? firstDecision.Allowed : secondDecision.Allowed;
        }

        internal static bool Obstructs(Collider collider, Rigidbody body)
        {
            if (!body || !collider || !collider.enabled || collider.isTrigger) return false;
            var attached = collider.attachedRigidbody;
            if (!attached && (!collider.gameObject.scene.IsValid() || !collider.gameObject.activeInHierarchy))
                attached = collider.GetComponentInParent<Rigidbody>(true);
            if (attached != body) return false;
            // A prefab or a temporarily inactive root can still be measured before it enters the physics scene.
            for (var child = collider.transform; child && child != body.transform; child = child.parent)
                if (!child.gameObject.activeSelf) return false;
            foreach (string name in EnvironmentLayers)
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0 && LayerContact(collider, body, layer, !Physics.GetIgnoreLayerCollision(collider.gameObject.layer, layer)).Allowed) return true;
            }
            return false;
        }

        internal static Size Measure(Character character, bool refresh = false)
        {
            var result = new Size { Bottom = float.PositiveInfinity, Top = float.NegativeInfinity };
            var body = character.GetComponent<Rigidbody>();
            if (!body) return result;
            var origin = character.transform.position;
            var forward = character.transform.forward; forward.y = 0f;
            var lateral = forward.sqrMagnitude > 1e-6f ? Vector3.Cross(Vector3.up, forward.normalized) : Vector3.right;
            foreach (var collider in Colliders(character, refresh))
            {
                if (!Obstructs(collider, body)) continue;
                if (collider is CapsuleCollider capsule)
                {
                    var scale = Abs(capsule.transform.lossyScale);
                    var axis = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
                    float axial = capsule.direction == 0 ? scale.x : capsule.direction == 1 ? scale.y : scale.z;
                    float radial = capsule.direction == 0 ? Mathf.Max(scale.y, scale.z) : capsule.direction == 1 ? Mathf.Max(scale.x, scale.z) : Mathf.Max(scale.x, scale.y);
                    float radius = capsule.radius * radial;
                    var segment = capsule.transform.TransformDirection(axis).normalized * Mathf.Max(0f, capsule.height * axial * .5f - radius);
                    var center = capsule.transform.TransformPoint(capsule.center);
                    Include(ref result, origin, center - segment, radius, lateral);
                    Include(ref result, origin, center + segment, radius, lateral);
                }
                else if (collider is SphereCollider sphere)
                {
                    var scale = Abs(sphere.transform.lossyScale);
                    Include(ref result, origin, sphere.transform.TransformPoint(sphere.center), sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)), lateral);
                }
                else if (collider is BoxCollider box)
                {
                    IncludeBox(ref result, origin, box.transform.localToWorldMatrix, new Bounds(box.center, box.size), lateral);
                }
                else if (collider is MeshCollider mesh && mesh.sharedMesh)
                {
                    var matrix = mesh.transform.localToWorldMatrix;
                    if (mesh.sharedMesh.isReadable)
                    {
                        var vertices = Meshes.GetValue(mesh.sharedMesh, source => new Vertices { Points = source.vertices });
                        foreach (var point in vertices.Points) Include(ref result, origin, matrix.MultiplyPoint3x4(point), 0f, lateral);
                    }
                    else IncludeBox(ref result, origin, matrix, mesh.sharedMesh.bounds, lateral);
                }
                else
                {
                    // A conservative envelope also covers an uncommon solid collider supported by the game engine.
                    IncludeBox(ref result, origin, Matrix4x4.identity, collider.bounds, lateral);
                }
            }
            return result;
        }

        private static Vector3 Abs(Vector3 value) => new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));

        private static void IncludeBox(ref Size size, Vector3 origin, Matrix4x4 matrix, Bounds bounds, Vector3 lateral)
        {
            for (int corner = 0; corner < 8; corner++)
                Include(ref size, origin, matrix.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f))), 0f, lateral);
        }

        private static void Include(ref Size size, Vector3 origin, Vector3 point, float radius, Vector3 lateral)
        {
            float horizontal = new Vector2(point.x - origin.x, point.z - origin.z).magnitude + radius;
            size.Radius = Mathf.Max(size.Radius, horizontal);
            // A walking creature turns to face its route, so the circle it sweeps through a passage follows its
            // width, not its length. The engine's own agents are sized this way; the full envelope stays for placement.
            size.HalfWidth = Mathf.Max(size.HalfWidth, Mathf.Abs(Vector3.Dot(point - origin, lateral)) + radius);
            size.Bottom = Mathf.Min(size.Bottom, point.y - origin.y - radius);
            size.Top = Mathf.Max(size.Top, point.y - origin.y + radius);
            var bounds = new Bounds(point, Vector3.one * radius * 2f);
            if (size.Valid) size.Bounds.Encapsulate(bounds);
            else { size.Valid = true; size.Bounds = bounds; }
        }
    }
}
