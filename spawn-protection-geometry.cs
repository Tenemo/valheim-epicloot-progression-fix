using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal struct ProtectionBoundarySegment
    {
        internal Vector2 First, Second;
        internal ProtectionBoundarySegment(Vector2 first, Vector2 second) { First = first; Second = second; }
    }

    internal struct ProtectionBoundaryLine : IEquatable<ProtectionBoundaryLine>
    {
        internal int HorizontalStep, VerticalStep;
        internal long Intercept;
        public bool Equals(ProtectionBoundaryLine other) => HorizontalStep == other.HorizontalStep &&
            VerticalStep == other.VerticalStep && Intercept == other.Intercept;
        public override bool Equals(object other) => other is ProtectionBoundaryLine line && Equals(line);
        public override int GetHashCode() => (HorizontalStep * 397 ^ VerticalStep) * 397 ^ Intercept.GetHashCode();
    }

    internal sealed class ProtectionContourMerge
    {
        internal readonly List<ProtectionBoundarySegment> Result = new List<ProtectionBoundarySegment>();
        private readonly List<ProtectionBoundarySegment>[] parts;
        private readonly ProtectionBoundaries boundaries = new ProtectionBoundaries();
        private int part, position;

        internal ProtectionContourMerge(List<ProtectionBoundarySegment>[] parts) { this.parts = parts; }
        internal bool Step()
        {
            if (part == parts.Length) return boundaries.Step(Result);
            var segments = parts[part];
            for (int count = 0; count < 256 && position < segments.Count; count++) boundaries.Add(segments[position++]);
            if (position == segments.Count) { part++; position = 0; }
            return true;
        }
    }

    // Directed interval cancellation handles a coarse triangle beside several finer triangles without internal walls.
    internal sealed class ProtectionBoundaries
    {
        private const int UnitsPerMeter = 4096;
        private readonly Dictionary<ProtectionBoundaryLine, Dictionary<int, int>> events = new Dictionary<ProtectionBoundaryLine, Dictionary<int, int>>();
        private readonly Stack<Dictionary<int, int>> available = new Stack<Dictionary<int, int>>();
        private readonly List<int> positions = new List<int>();
        private Dictionary<ProtectionBoundaryLine, Dictionary<int, int>>.Enumerator lines;
        private ProtectionBoundaryLine currentLine;
        private Dictionary<int, int> currentEvents;
        private int positionIndex, winding, start;
        private bool started;

        internal void Clear()
        {
            foreach (var values in events.Values) { values.Clear(); available.Push(values); }
            events.Clear(); positions.Clear(); started = false; currentEvents = null;
        }
        internal void Add(Vector3 first, Vector3 second) => Add(new Vector2(first.x, first.z), new Vector2(second.x, second.z));
        internal void Add(ProtectionBoundarySegment segment) => Add(segment.First, segment.Second);
        private void Add(Vector2 first, Vector2 second)
        {
            int firstX = Coordinate(first.x), firstZ = Coordinate(first.y), secondX = Coordinate(second.x), secondZ = Coordinate(second.y);
            int horizontal = secondX - firstX, vertical = secondZ - firstZ;
            if (horizontal == 0 && vertical == 0) return;
            int divisor = GreatestCommonDivisor(Math.Abs(horizontal), Math.Abs(vertical));
            horizontal /= divisor; vertical /= divisor;
            if (horizontal < 0 || (horizontal == 0 && vertical < 0)) { horizontal = -horizontal; vertical = -vertical; }
            var line = new ProtectionBoundaryLine { HorizontalStep = horizontal, VerticalStep = vertical,
                Intercept = (long)horizontal * firstZ - (long)vertical * firstX };
            if (!events.TryGetValue(line, out var values))
            { values = available.Count == 0 ? new Dictionary<int, int>() : available.Pop(); events.Add(line, values); }
            int firstPosition = horizontal == 0 ? firstZ : firstX, secondPosition = horizontal == 0 ? secondZ : secondX;
            int direction = secondPosition > firstPosition ? 1 : -1;
            Change(values, Math.Min(firstPosition, secondPosition), direction);
            Change(values, Math.Max(firstPosition, secondPosition), -direction);
        }
        private static int Coordinate(float value) => (int)Math.Round((double)value * UnitsPerMeter);
        private static int GreatestCommonDivisor(int first, int second)
        { while (second != 0) { int remainder = first % second; first = second; second = remainder; } return first; }
        private static void Change(Dictionary<int, int> values, int position, int delta)
        { values.TryGetValue(position, out int previous); values[position] = previous + delta; }
        private static Vector2 Point(ProtectionBoundaryLine line, int position)
        {
            if (line.HorizontalStep == 0) return new Vector2((float)(-(double)line.Intercept / line.VerticalStep / UnitsPerMeter), (float)position / UnitsPerMeter);
            return new Vector2((float)position / UnitsPerMeter,
                (float)(((double)line.Intercept + (double)line.VerticalStep * position) / line.HorizontalStep / UnitsPerMeter));
        }

        // One line or a small group of its events per step keeps sorting and contour construction off a single long frame.
        internal bool Step(List<ProtectionBoundarySegment> result)
        {
            if (!started) { lines = events.GetEnumerator(); started = true; }
            if (currentEvents == null)
            {
                if (!lines.MoveNext()) return false;
                currentLine = lines.Current.Key; currentEvents = lines.Current.Value;
                positions.Clear();
                foreach (var pair in currentEvents) if (pair.Value != 0) positions.Add(pair.Key);
                positions.Sort(); positionIndex = winding = 0;
                return true;
            }
            for (int count = 0; count < 32 && positionIndex < positions.Count; count++)
            {
                int position = positions[positionIndex++], previous = winding;
                winding += currentEvents[position];
                if (previous == 0 && winding != 0) start = position;
                else if (previous != 0 && (winding == 0 || Math.Sign(previous) != Math.Sign(winding)))
                {
                    var first = Point(currentLine, start); var second = Point(currentLine, position);
                    result.Add(previous > 0 ? new ProtectionBoundarySegment(first, second) : new ProtectionBoundarySegment(second, first));
                    if (winding != 0) start = position;
                }
            }
            if (positionIndex == positions.Count) currentEvents = null;
            return true;
        }
    }

    internal struct ProtectionShape : IEquatable<ProtectionShape>
    {
        internal const float Inset = 0.003f;
        private static readonly int ProtectionLayer = LayerMask.NameToLayer("character_trigger");
        internal enum ShapeKind { Inactive, Sphere, Capsule, Native }
        internal ShapeKind Kind;
        internal Bounds Bounds;
        internal Vector3 First, Second;
        internal float Radius;
        internal Matrix4x4 Transform;
        internal Collider Collider;

        internal static ProtectionShape Capture(EffectArea area, Collider collider)
        {
            if (area == null || collider == null || !collider.enabled || !area.gameObject.activeInHierarchy ||
                collider.gameObject.layer != ProtectionLayer || (!Physics.queriesHitTriggers && collider.isTrigger) ||
                (area.m_type & EffectArea.Type.PlayerBase) == 0) return default;
            var shape = new ProtectionShape { Bounds = collider.bounds, Collider = collider };
            if (collider is SphereCollider)
            {
                shape.Kind = ShapeKind.Sphere;
                shape.First = shape.Bounds.center;
                shape.Radius = shape.Bounds.extents.x;
            }
            else if (collider is CapsuleCollider capsule)
            {
                shape.Kind = ShapeKind.Capsule;
                var transform = collider.transform;
                var scale = transform.lossyScale;
                scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                int direction = capsule.direction;
                // Unity scales capsule dimensions on Y and X/Z before applying its selected direction.
                float along = scale.y;
                float across = Mathf.Max(scale.x, scale.z);
                shape.Radius = capsule.radius * across;
                var center = transform.TransformPoint(capsule.center);
                var axis = transform.rotation * (direction == 0 ? Vector3.right : direction == 1 ? Vector3.up : Vector3.forward);
                var offset = axis * Mathf.Max(0f, capsule.height * along * 0.5f - shape.Radius);
                shape.First = center - offset;
                shape.Second = center + offset;
            }
            else
            {
                shape.Kind = ShapeKind.Native;
                shape.Transform = collider.transform.localToWorldMatrix;
            }
            return shape.Kind != ShapeKind.Native && shape.Radius <= Inset ? default : shape;
        }

        internal bool Contains(Vector3 point)
        {
            if (Kind == ShapeKind.Inactive) return false;
            if (Kind == ShapeKind.Native)
                return Collider != null && Collider.ClosestPoint(point).Equals(point);
            var center = First;
            if (Kind == ShapeKind.Capsule)
            {
                var segment = Second - First;
                float length = segment.sqrMagnitude;
                if (length > 0f) center += segment * Mathf.Clamp01(Vector3.Dot(point - First, segment) / length);
            }
            // A small inward margin covers float rounding without coloring land outside the native volume.
            float radius = Radius - Inset;
            return (point - center).sqrMagnitude < radius * radius;
        }

        internal bool Covers(ProtectionTriangle triangle)
        {
            if (Kind == ShapeKind.Native)
            {
                if (!(Collider is BoxCollider) && !(Collider is MeshCollider mesh && mesh.convex)) return false;
                return NativeInterior(triangle.First) && NativeInterior(triangle.Second) && NativeInterior(triangle.Third);
            }
            return Contains(triangle.First) && Contains(triangle.Second) && Contains(triangle.Third);
        }

        internal bool MightIntersect(Bounds triangleBounds)
        {
            // The bounding sphere encloses every triangle point. Rejecting it cannot remove covered ground.
            float reach = triangleBounds.extents.magnitude + Inset;
            if (Kind == ShapeKind.Native)
            {
                if (Collider == null) return false;
                return (Collider.ClosestPoint(triangleBounds.center) - triangleBounds.center).sqrMagnitude <= reach * reach;
            }
            reach += Radius;
            return Kind != ShapeKind.Inactive && DistanceSquared(triangleBounds.center) <= reach * reach;
        }

        internal bool ContainsShape(ProtectionShape other)
        {
            if (other.Kind == ShapeKind.Inactive) return true;
            if ((Kind != ShapeKind.Sphere && Kind != ShapeKind.Capsule) ||
                (other.Kind != ShapeKind.Sphere && other.Kind != ShapeKind.Capsule)) return false;
            float reach = Radius - other.Radius;
            if (reach < 0f) return false;
            return DistanceSquared(other.First) <= reach * reach &&
                (other.Kind != ShapeKind.Capsule || DistanceSquared(other.Second) <= reach * reach);
        }
        private float DistanceSquared(Vector3 point)
        {
            var center = First;
            if (Kind == ShapeKind.Capsule)
            {
                var segment = Second - First;
                float length = segment.sqrMagnitude;
                if (length > 0f) center += segment * Mathf.Clamp01(Vector3.Dot(point - First, segment) / length);
            }
            return (point - center).sqrMagnitude;
        }

        private bool NativeInterior(Vector3 point) => Contains(point + Vector3.right * Inset) && Contains(point - Vector3.right * Inset) &&
            Contains(point + Vector3.up * Inset) && Contains(point - Vector3.up * Inset) && Contains(point + Vector3.forward * Inset) &&
            Contains(point - Vector3.forward * Inset);

        public bool Equals(ProtectionShape other) => Kind == other.Kind && Bounds.Equals(other.Bounds) &&
            First.Equals(other.First) && Second.Equals(other.Second) && Radius.Equals(other.Radius) &&
            Transform.Equals(other.Transform) && Collider == other.Collider;
    }

    internal struct ProtectionTriangle
    {
        internal Vector3 First, Second, Third;
        internal int Depth;
        internal ProtectionTriangle(Vector3 first, Vector3 second, Vector3 third, int depth = 0)
        { First = first; Second = second; Third = third; Depth = depth; }
        internal Bounds Bounds
        {
            get
            {
                var minimum = Vector3.Min(First, Vector3.Min(Second, Third));
                var maximum = Vector3.Max(First, Vector3.Max(Second, Third));
                return new Bounds((minimum + maximum) * 0.5f, maximum - minimum);
            }
        }
        internal float DiameterSquared => Mathf.Max((First - Second).sqrMagnitude,
            Mathf.Max((Second - Third).sqrMagnitude, (Third - First).sqrMagnitude));

        internal static int Clip(ProtectionTriangle triangle, float minimumX, float minimumZ, float maximumX, float maximumZ,
            Vector3[] polygon, Vector3[] scratch)
        {
            float lowX = Mathf.Min(triangle.First.x, Mathf.Min(triangle.Second.x, triangle.Third.x));
            float highX = Mathf.Max(triangle.First.x, Mathf.Max(triangle.Second.x, triangle.Third.x));
            float lowZ = Mathf.Min(triangle.First.z, Mathf.Min(triangle.Second.z, triangle.Third.z));
            float highZ = Mathf.Max(triangle.First.z, Mathf.Max(triangle.Second.z, triangle.Third.z));
            if (highX < minimumX || lowX > maximumX || highZ < minimumZ || lowZ > maximumZ) return 0;
            polygon[0] = triangle.First; polygon[1] = triangle.Second; polygon[2] = triangle.Third;
            if (lowX >= minimumX && highX <= maximumX && lowZ >= minimumZ && highZ <= maximumZ) return 3;
            int count = 3;
            for (int plane = 0; plane < 4 && count > 0; plane++)
            {
                bool horizontal = plane < 2, minimum = plane % 2 == 0;
                float limit = horizontal ? (minimum ? minimumX : maximumX) : (minimum ? minimumZ : maximumZ);
                int nextCount = 0;
                var previous = polygon[count - 1];
                float previousCoordinate = horizontal ? previous.x : previous.z;
                bool previousInside = minimum ? previousCoordinate >= limit : previousCoordinate <= limit;
                for (int index = 0; index < count; index++)
                {
                    var point = polygon[index];
                    float coordinate = horizontal ? point.x : point.z;
                    bool inside = minimum ? coordinate >= limit : coordinate <= limit;
                    if (inside != previousInside)
                    {
                        var crossing = Vector3.LerpUnclamped(previous, point, (limit - previousCoordinate) / (coordinate - previousCoordinate));
                        if (horizontal) crossing.x = limit; else crossing.z = limit;
                        scratch[nextCount++] = crossing;
                    }
                    if (inside) scratch[nextCount++] = point;
                    previous = point; previousCoordinate = coordinate; previousInside = inside;
                }
                count = nextCount;
                Array.Copy(scratch, polygon, count);
            }
            return count;
        }
    }

    internal sealed class ProtectionTerrain
    {
        private static readonly AccessTools.FieldRef<Heightmap, List<float>> Heights =
            AccessTools.FieldRefAccess<Heightmap, List<float>>("m_heights");
        internal readonly Heightmap Heightmap;
        internal readonly List<float> Values;
        internal readonly float MinimumX, MinimumZ, Size, Scale, Height;
        internal readonly int Width;
        internal bool Loaded = true;

        internal ProtectionTerrain(Heightmap heightmap)
        {
            Heightmap = heightmap;
            Values = new List<float>(Heights(heightmap));
            Width = heightmap.m_width;
            Scale = heightmap.m_scale;
            Size = Width * Scale;
            var position = heightmap.transform.position;
            MinimumX = position.x - Size * 0.5f;
            MinimumZ = position.z - Size * 0.5f;
            Height = position.y;
        }

        internal bool Sample(float x, float z, out float height)
        {
            height = 0f;
            if (!Loaded || x < MinimumX || z < MinimumZ || x > MinimumX + Size || z > MinimumZ + Size ||
                Values.Count != (Width + 1) * (Width + 1)) return false;
            float horizontal = (x - MinimumX) / Scale, vertical = (z - MinimumZ) / Scale;
            int column = Mathf.Clamp(Mathf.FloorToInt(horizontal), 0, Width - 1);
            int row = Mathf.Clamp(Mathf.FloorToInt(vertical), 0, Width - 1);
            horizontal -= column;
            vertical -= row;
            int first = row * (Width + 1) + column;
            float low = Values[first], right = Values[first + 1], above = Values[first + Width + 1], diagonal = Values[first + Width + 2];
            // Heightmap's collision diagonal joins the top-left and bottom-right vertices, rather than a bilinear surface.
            height = Height + (horizontal + vertical <= 1f
                ? low + horizontal * (right - low) + vertical * (above - low)
                : diagonal + (1f - horizontal) * (above - diagonal) + (1f - vertical) * (right - diagonal));
            return true;
        }
    }
}
