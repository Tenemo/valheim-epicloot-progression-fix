using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal abstract class ProtectionGeometryJob
    {
        internal volatile bool Cancelled;
        internal Exception Error;
        internal abstract bool Step();
    }

    internal sealed class ProtectionCoverage : ProtectionGeometryJob
    {
        private const int MaximumDepth = 12;
        private const float MaximumTriangleDiameter = 0.03f;
        internal readonly ProtectionChunk Chunk;
        internal readonly List<ProtectionGroundVertex> Vertices = new List<ProtectionGroundVertex>();
        internal readonly List<int> Indices = new List<int>();
        private readonly Dictionary<Vector3, int> vertexLookup = new Dictionary<Vector3, int>();
        internal readonly List<ProtectionBoundarySegment> Boundary = new List<ProtectionBoundarySegment>();
        internal float MinimumHeight = float.PositiveInfinity, MaximumHeight = float.NegativeInfinity;
        private readonly ProtectionShape[] shapes;
        private readonly Stack<ProtectionTriangle> patches;
        private readonly ProtectionBoundaries boundaries = new ProtectionBoundaries();
        private readonly int[][] candidates = new int[MaximumDepth + 1][];
        private readonly int[] candidateCounts = new int[MaximumDepth + 1];
        private bool outlining;

        internal ProtectionCoverage(ProtectionChunk chunk, ProtectionShape[] shapes, ProtectionTriangle[] terrain)
        {
            Chunk = chunk; this.shapes = shapes;
            Array.Sort(shapes, (first, second) => second.Radius.CompareTo(first.Radius));
            patches = shapes.Length == 0 ? new Stack<ProtectionTriangle>() : new Stack<ProtectionTriangle>(terrain);
            for (int depth = 0; depth < candidates.Length; depth++) candidates[depth] = new int[shapes.Length];
        }
        internal override bool Step()
        {
            if (Cancelled) return false;
            if (!outlining)
            {
                for (int count = 0; count < 8 && patches.Count > 0; count++) BuildPatch(patches.Pop());
                if (patches.Count == 0) outlining = true;
                return true;
            }
            return boundaries.Step(Boundary);
        }
        private void BuildPatch(ProtectionTriangle triangle)
        {
            var bounds = triangle.Bounds;
            int depth = triangle.Depth, sourceCount = depth == 0 ? shapes.Length : candidateCounts[depth - 1], possible = 0;
            for (int index = 0; index < sourceCount; index++)
            {
                int sourceIndex = depth == 0 ? index : candidates[depth - 1][index];
                var shape = shapes[sourceIndex];
                if (!shape.Bounds.Intersects(bounds)) continue;
                if (!shape.MightIntersect(bounds)) continue;
                if (shape.Covers(triangle)) { AddTriangle(triangle); return; }
                candidates[depth][possible++] = sourceIndex;
            }
            // A union of covered vertices cannot certify a whole patch. Unresolved patches never receive fill.
            if (possible == 0 || depth == MaximumDepth || triangle.DiameterSquared <= MaximumTriangleDiameter * MaximumTriangleDiameter) return;
            candidateCounts[depth] = possible;
            Vector3 first = (triangle.First + triangle.Second) * 0.5f, second = (triangle.Second + triangle.Third) * 0.5f,
                third = (triangle.Third + triangle.First) * 0.5f;
            patches.Push(new ProtectionTriangle(triangle.First, first, third, depth + 1));
            patches.Push(new ProtectionTriangle(first, triangle.Second, second, depth + 1));
            patches.Push(new ProtectionTriangle(third, second, triangle.Third, depth + 1));
            patches.Push(new ProtectionTriangle(first, second, third, depth + 1));
        }
        private void AddTriangle(ProtectionTriangle triangle)
        {
            AddVertex(triangle.First); AddVertex(triangle.Second); AddVertex(triangle.Third);
            boundaries.Add(triangle.First, triangle.Second); boundaries.Add(triangle.Second, triangle.Third); boundaries.Add(triangle.Third, triangle.First);
        }
        private void AddVertex(Vector3 point)
        {
            if (vertexLookup.TryGetValue(point, out int index)) { Indices.Add(index); return; }
            index = Vertices.Count; vertexLookup.Add(point, index); Indices.Add(index);
            MinimumHeight = Mathf.Min(MinimumHeight, point.y); MaximumHeight = Mathf.Max(MaximumHeight, point.y);
            Vertices.Add(new ProtectionGroundVertex { Position = point + Vector3.up * SpawnProtectionOverlay.GroundHighlightHeight,
                Color = SpawnProtectionOverlay.GroundVertexColor });
        }
    }

    // One worker uses immutable numeric snapshots. Unity collider fallback stays on the main-thread queue. It keeps normal
    // priority: every collection has to stop every managed thread, asleep or not, and a thread below normal that can't get a
    // processor on a busy machine holds the whole game for seconds, there and while it holds any of the runtime's locks.
    internal sealed class ProtectionGeometryWorker
    {
        private readonly ConcurrentQueue<ProtectionGeometryJob> pending = new ConcurrentQueue<ProtectionGeometryJob>();
        private readonly ConcurrentQueue<ProtectionGeometryJob> completed = new ConcurrentQueue<ProtectionGeometryJob>();
        private readonly AutoResetEvent ready = new AutoResetEvent(false);
        private volatile bool stopping;
        private int outstanding;
        internal int Pending => Volatile.Read(ref outstanding);
        internal ProtectionGeometryWorker()
        { new Thread(Run) { IsBackground = true, Name = "Spawn protection geometry" }.Start(); }
        internal void Enqueue(ProtectionGeometryJob job)
        { Interlocked.Increment(ref outstanding); pending.Enqueue(job); ready.Set(); }
        internal bool TryCompleted(out ProtectionGeometryJob job)
        {
            if (!completed.TryDequeue(out job)) return false;
            Interlocked.Decrement(ref outstanding); return true;
        }
        internal void Stop() { stopping = true; ready.Set(); }
        private void Run()
        {
            try
            {
                while (!stopping)
                {
                    if (!pending.TryDequeue(out var job)) { ready.WaitOne(); continue; }
                    try { while (!stopping && job.Step()) { } }
                    catch (Exception error) { job.Error = error; }
                    completed.Enqueue(job);
                }
            }
            finally { ready.Dispose(); }
        }
    }

    internal struct ProtectionRenderChunk
    {
        internal ProtectionChunk Chunk;
        internal int StartX, StartZ;
        internal float MinimumHeight, MaximumHeight;
        internal List<ProtectionGroundVertex> GroundVertices;
        internal List<int> GroundIndices;
        internal List<ProtectionBoundarySegment> Boundary;
        internal ProtectionRenderChunk(ProtectionChunk chunk)
        {
            Chunk = chunk; StartX = chunk.StartX; StartZ = chunk.StartZ;
            MinimumHeight = chunk.MinimumHeight; MaximumHeight = chunk.MaximumHeight;
            GroundVertices = chunk.GroundVertices; GroundIndices = chunk.GroundIndices; Boundary = chunk.Boundary;
        }
    }

    // The worker reads immutable numeric snapshots; only the main thread accesses Unity objects and uploads meshes.
    internal sealed class ProtectionMeshBuild : ProtectionGeometryJob
    {
        private const int Cells = SpawnProtectionOverlay.Cells, ChunkCells = SpawnProtectionOverlay.ChunkCells;
        private const float BorderHeight = SpawnProtectionOverlay.BorderHeight, GroundHighlightHeight = SpawnProtectionOverlay.GroundHighlightHeight;
        private static readonly Color32 GroundVertexColor = SpawnProtectionOverlay.GroundVertexColor;
        internal readonly List<ProtectionGroundVertex> GroundVertices = new List<ProtectionGroundVertex>();
        internal readonly List<int> GroundIndices = new List<int>();
        internal readonly List<ProtectionBorderVertex> BorderVertices = new List<ProtectionBorderVertex>();
        internal readonly List<ProtectionDrawRange> DrawRanges = new List<ProtectionDrawRange>();
        internal List<ProtectionBoundarySegment> Boundary;
        internal float MinimumHeight = float.PositiveInfinity, MaximumHeight = float.NegativeInfinity;
        private readonly ProtectionRenderChunk[] chunks;
        private readonly ProtectionTerrain[] terrain;
        private readonly int minimumX, minimumZ;
        private readonly Dictionary<Vector3, int> croppedGroundVertices = new Dictionary<Vector3, int>();
        private readonly Vector3[] polygon = new Vector3[8], scratch = new Vector3[8];
        private readonly ProtectionContourMerge contours;
        private int meshPhase, meshChunk, meshPosition, chunkStart, chunkVertexStart;

        internal ProtectionMeshBuild(ProtectionChunk[] renderChunks, ProtectionTerrain[] terrain, int minimumX, int minimumZ,
            List<ProtectionBoundarySegment> boundary, bool contoursChanged)
        {
            this.terrain = terrain; this.minimumX = minimumX; this.minimumZ = minimumZ; Boundary = boundary;
            chunks = new ProtectionRenderChunk[renderChunks.Length];
            var parts = new List<ProtectionBoundarySegment>[renderChunks.Length];
            int count = 0;
            for (int index = 0; index < renderChunks.Length; index++)
                if (renderChunks[index] != null && renderChunks[index].Ready)
                { chunks[index] = new ProtectionRenderChunk(renderChunks[index]); parts[count++] = chunks[index].Boundary; }
            if (contoursChanged) { Array.Resize(ref parts, count); contours = new ProtectionContourMerge(parts); }
        }
        internal override bool Step()
        {
            if (Cancelled) return false;
            if (meshPhase == 0) { BuildGround(); return true; }
            if (meshPhase == 1)
            {
                if (contours != null)
                { if (contours.Step()) return true; Boundary = contours.Result; }
                meshPhase = 3; meshPosition = 0;
            }
            for (int count = 0; count < 16 && meshPosition < Boundary.Count; count++) AddWall(Boundary[meshPosition++]);
            return meshPosition < Boundary.Count;
        }
        private void BuildGround()
        {
            if (meshChunk == chunks.Length) { meshPhase++; meshChunk = meshPosition = 0; return; }
            var chunk = chunks[meshChunk];
            if (chunk.Chunk != null)
            {
                bool complete = chunk.StartX >= minimumX && chunk.StartZ >= minimumZ &&
                    chunk.StartX + ChunkCells <= minimumX + Cells && chunk.StartZ + ChunkCells <= minimumZ + Cells;
                if (complete)
                {
                    if (meshPosition == 0)
                    {
                        chunkVertexStart = GroundVertices.Count; GroundVertices.AddRange(chunk.GroundVertices);
                        MinimumHeight = Mathf.Min(MinimumHeight, chunk.MinimumHeight + GroundHighlightHeight);
                        MaximumHeight = Mathf.Max(MaximumHeight, chunk.MaximumHeight + GroundHighlightHeight);
                    }
                    for (int count = 0; count < 2048 && meshPosition < chunk.GroundIndices.Count; count++)
                        GroundIndices.Add(chunkVertexStart + chunk.GroundIndices[meshPosition++]);
                }
                else for (int count = 0; count < 128 && meshPosition < chunk.GroundIndices.Count; count++)
                {
                    var triangle = new ProtectionTriangle(chunk.GroundVertices[chunk.GroundIndices[meshPosition]].Position,
                        chunk.GroundVertices[chunk.GroundIndices[meshPosition + 1]].Position,
                        chunk.GroundVertices[chunk.GroundIndices[meshPosition + 2]].Position);
                    meshPosition += 3;
                    int points = ProtectionTriangle.Clip(triangle, minimumX, minimumZ, minimumX + Cells, minimumZ + Cells, polygon, scratch);
                    for (int vertex = 1; vertex + 1 < points; vertex++)
                    {
                        AddGroundVertex(polygon[0]); AddGroundVertex(polygon[vertex]); AddGroundVertex(polygon[vertex + 1]);
                    }
                }
                if (meshPosition < chunk.GroundIndices.Count) return;
                DrawRanges.Add(new ProtectionDrawRange { Chunk = chunk.Chunk, Start = chunkStart, Count = GroundIndices.Count - chunkStart });
            }
            meshChunk++; meshPosition = 0; chunkStart = GroundIndices.Count;
        }
        private void AddGroundVertex(Vector3 point)
        {
            if (croppedGroundVertices.TryGetValue(point, out int index)) { GroundIndices.Add(index); return; }
            croppedGroundVertices.Add(point, GroundVertices.Count);
            GroundIndices.Add(GroundVertices.Count);
            GroundVertices.Add(new ProtectionGroundVertex { Position = point, Color = GroundVertexColor });
            MinimumHeight = Mathf.Min(MinimumHeight, point.y); MaximumHeight = Mathf.Max(MaximumHeight, point.y);
        }
        private bool Ground(Vector2 horizontal, out Vector3 point)
        {
            foreach (var surface in terrain)
                if (surface.Sample(horizontal.x, horizontal.y, out float height)) { point = new Vector3(horizontal.x, height, horizontal.y); return true; }
            point = default; return false;
        }
        private void AddWall(ProtectionBoundarySegment segment)
        {
            Vector2 first = segment.First, second = segment.Second;
            Vector2 direction = second - first;
            float low = 0f, high = 1f;
            for (int axis = 0; axis < 2; axis++)
            {
                float origin = axis == 0 ? first.x : first.y, delta = axis == 0 ? direction.x : direction.y;
                float minimum = axis == 0 ? minimumX : minimumZ, maximum = minimum + Cells;
                if (delta == 0f) { if (origin < minimum || origin > maximum) return; }
                else
                {
                    float near = (minimum - origin) / delta, far = (maximum - origin) / delta;
                    low = Mathf.Max(low, Mathf.Min(near, far)); high = Mathf.Min(high, Mathf.Max(near, far));
                    if (low >= high) return;
                }
            }
            second = first + direction * high; first += direction * low;
            // The display window is a crop, not a protection boundary.
            if ((first.x == second.x && (first.x == minimumX || first.x == minimumX + Cells)) ||
                (first.y == second.y && (first.y == minimumZ || first.y == minimumZ + Cells))) return;
            if (!Ground(first, out var firstPoint) || !Ground(second, out var secondPoint)) return;
            firstPoint.y += 0.035f; secondPoint.y += 0.035f;
            float firstCoordinate = (first.x + first.y) * 0.8f, secondCoordinate = (second.x + second.y) * 0.8f;
            var bottom = new Color32(255, 255, 255, 191); var top = new Color32(255, 255, 255, 255);
            BorderVertices.Add(new ProtectionBorderVertex { Position = firstPoint, Color = bottom, Coordinates = new Vector2(firstCoordinate, 0f) });
            BorderVertices.Add(new ProtectionBorderVertex { Position = secondPoint, Color = bottom, Coordinates = new Vector2(secondCoordinate, 0f) });
            BorderVertices.Add(new ProtectionBorderVertex { Position = firstPoint + Vector3.up * BorderHeight, Color = top, Coordinates = new Vector2(firstCoordinate, BorderHeight * 0.8f) });
            BorderVertices.Add(new ProtectionBorderVertex { Position = secondPoint + Vector3.up * BorderHeight, Color = top, Coordinates = new Vector2(secondCoordinate, BorderHeight * 0.8f) });
        }
    }

    internal sealed class ProtectionTerrainBuild : ProtectionGeometryJob
    {
        internal readonly ProtectionChunk Chunk;
        internal ProtectionTriangle[] Triangles;
        private readonly int StartX, StartZ;
        private readonly ProtectionTerrain[] surfaces;
        private readonly List<ProtectionTriangle> triangles = new List<ProtectionTriangle>();
        private readonly Vector3[] polygon = new Vector3[8], scratch = new Vector3[8];
        private int surfaceIndex, terrainRow = -1, firstColumn, lastColumn, lastRow;
        internal ProtectionTerrainBuild(ProtectionChunk chunk, ProtectionTerrain[] surfaces)
        { Chunk = chunk; StartX = chunk.StartX; StartZ = chunk.StartZ; this.surfaces = surfaces; }
        internal override bool Step()
        {
            if (Cancelled) return false;
            if (surfaceIndex == surfaces.Length) { Triangles = triangles.ToArray(); return false; }
            SampleTerrainRow(); return true;
        }
        private void SampleTerrainRow()
        {
            var surface = surfaces[surfaceIndex];
            if (terrainRow < 0)
            {
                firstColumn = Mathf.Clamp(Mathf.FloorToInt((StartX - surface.MinimumX) / surface.Scale), 0, surface.Width - 1);
                lastColumn = Mathf.Clamp(Mathf.CeilToInt((StartX + SpawnProtectionOverlay.ChunkCells - surface.MinimumX) / surface.Scale) - 1, 0, surface.Width - 1);
                terrainRow = Mathf.Clamp(Mathf.FloorToInt((StartZ - surface.MinimumZ) / surface.Scale), 0, surface.Width - 1);
                lastRow = Mathf.Clamp(Mathf.CeilToInt((StartZ + SpawnProtectionOverlay.ChunkCells - surface.MinimumZ) / surface.Scale) - 1, 0, surface.Width - 1);
            }
            if (!surface.Loaded || surface.Values.Count != (surface.Width + 1) * (surface.Width + 1))
            { surfaceIndex++; terrainRow = -1; return; }
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                Vector3 first = TerrainPoint(surface, column, terrainRow), second = TerrainPoint(surface, column + 1, terrainRow);
                Vector3 third = TerrainPoint(surface, column, terrainRow + 1), fourth = TerrainPoint(surface, column + 1, terrainRow + 1);
                CacheTriangle(new ProtectionTriangle(first, third, second));
                CacheTriangle(new ProtectionTriangle(fourth, second, third));
            }
            if (++terrainRow > lastRow) { surfaceIndex++; terrainRow = -1; }
        }
        private static Vector3 TerrainPoint(ProtectionTerrain surface, int column, int row) => new Vector3(surface.MinimumX + column * surface.Scale,
            surface.Height + surface.Values[row * (surface.Width + 1) + column], surface.MinimumZ + row * surface.Scale);
        private void CacheTriangle(ProtectionTriangle triangle)
        {
            int count = ProtectionTriangle.Clip(triangle, StartX, StartZ, StartX + SpawnProtectionOverlay.ChunkCells, StartZ + SpawnProtectionOverlay.ChunkCells, polygon, scratch);
            for (int index = 1; index + 1 < count; index++)
                if (Vector3.Cross(polygon[index] - polygon[0], polygon[index + 1] - polygon[0]).sqrMagnitude > 0.000000000001f)
                    triangles.Add(new ProtectionTriangle(polygon[0], polygon[index], polygon[index + 1]));
        }
    }
}
