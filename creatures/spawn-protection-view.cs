using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EpicLootProgressionFix
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProtectionGroundVertex
    {
        internal Vector3 Position;
        internal Color32 Color;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProtectionBorderVertex
    {
        internal Vector3 Position;
        internal Color32 Color;
        internal Vector2 Coordinates;
    }

    internal sealed class ProtectionChunk
    {
        private const int Cells = SpawnProtectionOverlay.ChunkCells;
        internal readonly int StartX, StartZ;
        internal bool Queued, Disposed, Ready;
        internal int DrawStart, DrawCount;
        internal float MinimumHeight, MaximumHeight;
        internal readonly HashSet<ProtectionSource> Sources = new HashSet<ProtectionSource>();
        internal List<ProtectionGroundVertex> GroundVertices = new List<ProtectionGroundVertex>();
        internal List<int> GroundIndices = new List<int>();
        internal List<ProtectionBoundarySegment> Boundary = new List<ProtectionBoundarySegment>();
        private readonly SpawnProtectionOverlay overlay;
        private readonly List<ProtectionTerrain> surfaces = new List<ProtectionTerrain>();
        private ProtectionTriangle[] terrainTriangles = Array.Empty<ProtectionTriangle>();
        private ProtectionTerrainBuild terrainBuild;
        private readonly List<ProtectionShape> shapes = new List<ProtectionShape>();
        private bool terrainDirty = true, geometryDirty = true;
        private ProtectionCoverage coverage;
        private bool mainThreadCoverage;

        internal ProtectionChunk(SpawnProtectionOverlay overlay, Vector2Int coordinates)
        { this.overlay = overlay; StartX = coordinates.x * Cells; StartZ = coordinates.y * Cells; }
        internal bool Overlaps(ProtectionShape shape) => shape.Kind != ProtectionShape.ShapeKind.Inactive &&
            shape.Bounds.min.x <= StartX + Cells && shape.Bounds.max.x >= StartX &&
            shape.Bounds.min.z <= StartZ + Cells && shape.Bounds.max.z >= StartZ;
        internal bool Overlaps(ProtectionTerrain surface) => surface != null &&
            surface.MinimumX <= StartX + Cells && surface.MinimumX + surface.Size >= StartX &&
            surface.MinimumZ <= StartZ + Cells && surface.MinimumZ + surface.Size >= StartZ;
        internal void Queue(ProtectionSource source, ProtectionShape previous = default)
        {
            if (Overlaps(source.Shape)) Sources.Add(source); else Sources.Remove(source);
            foreach (var other in Sources)
                if (other != source && other.Shape.ContainsShape(previous) && other.Shape.ContainsShape(source.Shape)) return;
            Invalidate();
        }
        internal void InvalidateTerrain()
        {
            terrainDirty = true;
            if (terrainBuild != null) { terrainBuild.Cancelled = true; terrainBuild = null; }
            Invalidate();
        }
        internal void Evict()
        { Disposed = true; if (coverage != null) coverage.Cancelled = true; if (terrainBuild != null) terrainBuild.Cancelled = true; }
        private void Invalidate()
        {
            geometryDirty = true; Ready = false;
            if (coverage != null) { coverage.Cancelled = true; coverage = null; }
            overlay.InvalidateDraw(this);
        }

        internal bool Step()
        {
            if (terrainDirty)
            {
                terrainDirty = false; surfaces.Clear();
                foreach (var surface in overlay.Terrain) if (Overlaps(surface) && surface.Loaded) surfaces.Add(surface);
                terrainBuild = new ProtectionTerrainBuild(this, surfaces.ToArray()); overlay.EnqueueGeometry(terrainBuild);
                return false;
            }
            if (terrainBuild != null) return false;
            if (geometryDirty && coverage == null)
            {
                shapes.Clear();
                bool native = false;
                foreach (var source in Sources) if (Overlaps(source.Shape)) { shapes.Add(source.Shape); native |= source.Shape.Kind == ProtectionShape.ShapeKind.Native; }
                coverage = new ProtectionCoverage(this, shapes.ToArray(), terrainTriangles);
                mainThreadCoverage = native;
                if (!native) { overlay.EnqueueGeometry(coverage); return false; }
            }
            if (coverage != null)
            {
                if (!mainThreadCoverage) return false;
                if (coverage.Step()) return true;
                AcceptCoverage(coverage);
            }
            return false;
        }

        internal void AcceptTerrain(ProtectionTerrainBuild result)
        {
            if (terrainBuild != result || result.Cancelled || Disposed) return;
            if (result.Error != null) throw new InvalidOperationException("Spawn protection terrain sampling failed.", result.Error);
            terrainTriangles = result.Triangles; terrainBuild = null;
            overlay.QueueChunk(this);
        }
        internal void AcceptCoverage(ProtectionCoverage result)
        {
            if (coverage != result || result.Cancelled || Disposed) return;
            if (result.Error != null) throw new InvalidOperationException("Spawn protection coverage construction failed.", result.Error);
            GroundVertices = result.Vertices; GroundIndices = result.Indices; Boundary = result.Boundary;
            MinimumHeight = result.MinimumHeight; MaximumHeight = result.MaximumHeight;
            coverage = null; geometryDirty = false; Ready = true;
            overlay.ChunkReady(this);
        }
    }

    internal sealed class ProtectionSource
    {
        internal readonly EffectArea Area;
        internal Collider Collider;
        internal ProtectionShape Shape;
        internal ProtectionSource(EffectArea area) { Area = area; Collider = area.GetComponent<Collider>(); }
    }
    internal struct ProtectionDrawRange
    {
        internal ProtectionChunk Chunk;
        internal int Start, Count;
    }

    // Geometry, contour merging and GPU uploads are queued; a complete back buffer replaces both visible meshes together.
    internal sealed class SpawnProtectionOverlay : MonoBehaviour
    {
        private static SpawnProtectionOverlay instance;
        private static readonly Func<Player, bool> TakeInput = AccessTools.MethodDelegate<Func<Player, bool>>(AccessTools.Method(typeof(Player), "TakeInput"));
        internal const int Cells = 160, ChunkCells = 32, RenderSide = 6;
        internal const float Reach = Cells / 2f, BorderHeight = 2f, GroundHighlightHeight = 0.20f;
        // Ground vertices are white, so the ground material's configured color is the ground's and changes apply at once.
        internal static readonly Color32 GroundVertexColor = new Color32(255, 255, 255, 255);
        private const MeshUpdateFlags UploadFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;
        private static readonly VertexAttributeDescriptor[] GroundLayout = {
            new VertexAttributeDescriptor(VertexAttribute.Position), new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4) };
        private static readonly VertexAttributeDescriptor[] BorderLayout = {
            new VertexAttributeDescriptor(VertexAttribute.Position), new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2) };
        private Player owner;
        internal bool Requested;
        internal MeshRenderer BorderRenderer, GroundRenderer;
        internal Mesh BorderMesh, GroundMesh;
        private Mesh borderBack, groundBack;
        private MeshFilter borderFilter, groundFilter;
        private Material groundTint, borderTint;
        private Texture2D borderStripes;
        private bool seeded, hasView, meshDirty, meshStarted, contoursDirty = true;
        private float nextMoveRefresh;
        private int auditIndex, minimumX, minimumZ;
        private Vector3 cachedCenter;
        private readonly Dictionary<EffectArea, ProtectionSource> sourceLookup = new Dictionary<EffectArea, ProtectionSource>();
        private readonly List<ProtectionSource> sources = new List<ProtectionSource>();
        private readonly Queue<EffectArea> sourceChanges = new Queue<EffectArea>();
        private readonly HashSet<EffectArea> queuedSources = new HashSet<EffectArea>();
        private readonly List<ProtectionTerrain> terrain = new List<ProtectionTerrain>();
        private readonly Dictionary<Vector2Int, ProtectionChunk> chunks = new Dictionary<Vector2Int, ProtectionChunk>();
        private readonly Queue<ProtectionChunk> work = new Queue<ProtectionChunk>();
        private readonly List<Vector2Int> removedChunks = new List<Vector2Int>();
        private readonly ProtectionChunk[] renderChunks = new ProtectionChunk[RenderSide * RenderSide];
        private readonly ProtectionChunk[] previousRenderChunks = new ProtectionChunk[RenderSide * RenderSide];
        private List<ProtectionGroundVertex> groundBuild = new List<ProtectionGroundVertex>();
        private List<int> groundIndexBuild = new List<int>();
        private List<ProtectionBorderVertex> borderBuild = new List<ProtectionBorderVertex>();
        private List<ProtectionBoundarySegment> mergedBoundary = new List<ProtectionBoundarySegment>();
        private List<ProtectionDrawRange> drawRanges = new List<ProtectionDrawRange>();
        private ProtectionMeshBuild meshBuild;
        private readonly ProtectionGeometryWorker geometryWorker = new ProtectionGeometryWorker();
        private readonly Dictionary<Mesh, int> indexCapacities = new Dictionary<Mesh, int>();
        private ProtectionGroundVertex[] groundData = Array.Empty<ProtectionGroundVertex>();
        private ProtectionBorderVertex[] borderData = Array.Empty<ProtectionBorderVertex>();
        private int[] groundIndices = Array.Empty<int>(), borderIndices = Array.Empty<int>();
        private readonly int[] zeroIndices = new int[2048];
        private int meshPhase, uploadPosition, visibleBorderIndices;
        private float minimumHeight, maximumHeight;

        private static bool HoldingHammer(Player player) => player != null && !player.IsDead() &&
            GameFields.RightItem(player)?.m_shared.m_name == "$item_hammer";
        internal static bool ShortcutHeld(Player player) => HoldingHammer(player) && Input.GetKey(Plugin.SpawnProtectionToggleKey.Value) &&
            (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));

        internal static void Tick()
        {
            var player = Player.m_localPlayer;
            if (player == null || ZNet.instance == null || ZNet.instance.IsDedicated())
            {
                if (instance != null) Destroy(instance.gameObject);
                instance = null;
                return;
            }
            if (instance == null)
            {
                instance = new GameObject("Spawn protection overlay").AddComponent<SpawnProtectionOverlay>();
                instance.owner = player;
            }
        }

        private void Awake()
        {
            GroundMesh = CreateMesh("Spawn protection protected ground");
            BorderMesh = CreateMesh("Spawn protection striped border");
            groundBack = CreateMesh("Spawn protection ground back buffer");
            borderBack = CreateMesh("Spawn protection border back buffer");
            groundTint = new Material(Shader.Find("Sprites/Default")) { name = "Spawn protection orange ground", color = Color.white };
            borderStripes = new Texture2D(64, 64, TextureFormat.RGBA32, false)
                { name = "Spawn protection diagonal stripes", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            borderTint = new Material(groundTint) { name = "Spawn protection striped border", mainTexture = borderStripes };
            ApplyColors(null, EventArgs.Empty);
            foreach (var color in new[] { Plugin.SpawnProtectionGroundColor, Plugin.SpawnProtectionStripeColor, Plugin.SpawnProtectionStripeShadowColor })
                color.SettingChanged += ApplyColors;
            borderFilter = gameObject.AddComponent<MeshFilter>(); borderFilter.sharedMesh = BorderMesh;
            BorderRenderer = gameObject.AddComponent<MeshRenderer>(); BorderRenderer.sharedMaterial = borderTint;
            var groundObject = new GameObject("Protected ground highlight");
            groundObject.transform.SetParent(transform, false);
            groundFilter = groundObject.AddComponent<MeshFilter>(); groundFilter.sharedMesh = GroundMesh;
            GroundRenderer = groundObject.AddComponent<MeshRenderer>(); GroundRenderer.sharedMaterial = groundTint;
            foreach (var renderer in new[] { BorderRenderer, GroundRenderer })
            {
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false; renderer.enabled = false;
            }
        }
        // The border material stays white: its stripes texture carries both stripe colors.
        private void ApplyColors(object sender, EventArgs arguments)
        {
            groundTint.color = Plugin.SpawnProtectionGroundColor.Value;
            Color bright = Plugin.SpawnProtectionStripeColor.Value, dark = Plugin.SpawnProtectionStripeShadowColor.Value;
            var pixels = new Color[64 * 64];
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++) pixels[y * 64 + x] = (x + y) % 64 < 32 ? bright : dark;
            borderStripes.SetPixels(pixels); borderStripes.Apply();
        }
        private static Mesh CreateMesh(string name)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32, subMeshCount = 1 };
            mesh.MarkDynamic();
            return mesh;
        }
        private void OnDestroy()
        {
            foreach (var color in new[] { Plugin.SpawnProtectionGroundColor, Plugin.SpawnProtectionStripeColor, Plugin.SpawnProtectionStripeShadowColor })
                color.SettingChanged -= ApplyColors;
            geometryWorker.Stop();
            Destroy(GroundMesh); Destroy(BorderMesh); Destroy(groundBack); Destroy(borderBack);
            Destroy(groundTint); Destroy(borderTint); Destroy(borderStripes);
        }

        private void Seed()
        {
            if (seeded) return;
            seeded = true;
            foreach (var area in EffectArea.GetAllAreas())
                if ((area.m_type & EffectArea.Type.PlayerBase) != 0) QueueSource(area);
            foreach (var heightmap in Heightmap.GetAllHeightmaps())
                if (!heightmap.IsDistantLod) terrain.Add(new ProtectionTerrain(heightmap));
        }
        private void Update()
        {
            long started = Stopwatch.GetTimestamp();
            var player = Player.m_localPlayer;
            if (player != owner) { owner = player; Requested = false; }
            bool hammer = HoldingHammer(player);
            if (!hammer) Requested = false;
            else if (Input.GetKeyDown(Plugin.SpawnProtectionToggleKey.Value) && ShortcutHeld(player) && TakeInput(player)) Requested = !Requested;
            bool visible = Requested && hammer;
            if (BorderRenderer.enabled != visible) BorderRenderer.enabled = visible;
            if (GroundRenderer.enabled != visible) GroundRenderer.enabled = visible;
            if (!Requested)
            {
                // Like terrain, areas keep changing while the overlay is hidden; capturing them drops destroyed ones.
                if (seeded) CaptureQueued(started + Stopwatch.Frequency / 20000);
                return;
            }
            UpdateCache(player.transform.position, started + Stopwatch.Frequency / 4000);
        }

        private void UpdateCache(Vector3 position, long deadline)
        {
            Seed();
            if (!hasView || (!meshDirty && Time.unscaledTime >= nextMoveRefresh &&
                (Mathf.Abs(position.x - cachedCenter.x) >= 1f || Mathf.Abs(position.z - cachedCenter.z) >= 1f))) SetView(position);
            // Lifecycle callbacks queue ordinary changes immediately; this small audit catches silent collider edits.
            int auditCount = Math.Min(8, Math.Max(1, Mathf.CeilToInt(sources.Count * Time.unscaledDeltaTime)));
            for (int count = 0; count < auditCount && sources.Count > 0; count++)
            {
                if (auditIndex >= sources.Count) auditIndex = 0;
                Capture(sources[auditIndex++].Area);
            }
            Process(deadline);
        }
        private void QueueSource(EffectArea area)
        {
            if (!ReferenceEquals(area, null) && queuedSources.Add(area)) sourceChanges.Enqueue(area);
        }
        // Until the overlay is first shown, Seed reads every current area; after that only player base areas and areas it
        // already tracks can change it. Queuing every other area, as each fire passing in and out of range did, kept their
        // destroyed objects referenced for the rest of the session while the overlay stayed hidden.
        internal static void AreaChanged(EffectArea area)
        {
            if (instance == null || !instance.seeded || ReferenceEquals(area, null)) return;
            if ((area.m_type & EffectArea.Type.PlayerBase) != 0 || instance.sourceLookup.ContainsKey(area)) instance.QueueSource(area);
        }
        internal static void ColliderChanged(Collider collider)
        {
            if (instance != null && collider != null)
            {
                var area = collider.GetComponent<EffectArea>();
                if (area != null) instance.Capture(area);
            }
        }
        private void Capture(EffectArea area)
        {
            if (!sourceLookup.TryGetValue(area, out var source))
            {
                if (area == null || (area.m_type & EffectArea.Type.PlayerBase) == 0) return;
                source = new ProtectionSource(area);
                sourceLookup.Add(area, source); sources.Add(source);
            }
            if (area != null && source.Collider == null) source.Collider = area.GetComponent<Collider>();
            var shape = ProtectionShape.Capture(area, source.Collider);
            if (!shape.Equals(source.Shape))
            {
                var previous = source.Shape; source.Shape = shape;
                foreach (var chunk in chunks.Values)
                    if (chunk.Overlaps(previous) || chunk.Overlaps(shape) || chunk.Sources.Contains(source))
                    { chunk.Queue(source, previous); QueueChunk(chunk); }
            }
            if (area == null) { sourceLookup.Remove(area); sources.Remove(source); }
        }
        internal void EnqueueGeometry(ProtectionGeometryJob job) => geometryWorker.Enqueue(job);
        internal IEnumerable<ProtectionTerrain> Terrain => terrain;

        internal static void TerrainChanged(Heightmap heightmap, bool removed = false)
        {
            if (instance == null || !instance.seeded) return;
            ProtectionTerrain previous = null;
            for (int index = 0; index < instance.terrain.Count; index++)
                if (instance.terrain[index].Heightmap == heightmap)
                { previous = instance.terrain[index]; previous.Loaded = false; instance.terrain.RemoveAt(index); break; }
            ProtectionTerrain current = null;
            if (!removed && heightmap != null && !heightmap.IsDistantLod)
            { current = new ProtectionTerrain(heightmap); instance.terrain.Add(current); }
            foreach (var chunk in instance.chunks.Values)
                if (chunk.Overlaps(previous) || chunk.Overlaps(current))
                { chunk.InvalidateTerrain(); instance.QueueChunk(chunk); }
        }

        private void SetView(Vector3 center)
        {
            cachedCenter = center;
            minimumX = Mathf.FloorToInt(center.x) - (int)Reach;
            minimumZ = Mathf.FloorToInt(center.z) - (int)Reach;
            hasView = true; nextMoveRefresh = Time.unscaledTime + 1f;
            int firstX = Mathf.FloorToInt((minimumX - ChunkCells) / (float)ChunkCells);
            int firstZ = Mathf.FloorToInt((minimumZ - ChunkCells) / (float)ChunkCells);
            int lastX = Mathf.FloorToInt((minimumX + Cells + ChunkCells - 1) / (float)ChunkCells);
            int lastZ = Mathf.FloorToInt((minimumZ + Cells + ChunkCells - 1) / (float)ChunkCells);
            removedChunks.Clear();
            foreach (var pair in chunks)
                if (pair.Key.x < firstX || pair.Key.x > lastX || pair.Key.y < firstZ || pair.Key.y > lastZ) removedChunks.Add(pair.Key);
            foreach (var key in removedChunks) { var chunk = chunks[key]; chunk.Evict(); InvalidateDraw(chunk); chunks.Remove(key); }
            for (int z = firstZ; z <= lastZ; z++)
                for (int x = firstX; x <= lastX; x++)
                {
                    var key = new Vector2Int(x, z);
                    if (chunks.ContainsKey(key)) continue;
                    var chunk = new ProtectionChunk(this, key); chunks.Add(key, chunk);
                    foreach (var source in sources) if (chunk.Overlaps(source.Shape)) chunk.Queue(source);
                    QueueChunk(chunk);
                }
            Array.Copy(renderChunks, previousRenderChunks, renderChunks.Length);
            Array.Clear(renderChunks, 0, renderChunks.Length);
            int renderFirstX = Mathf.FloorToInt(minimumX / (float)ChunkCells), renderFirstZ = Mathf.FloorToInt(minimumZ / (float)ChunkCells);
            int renderLastX = Mathf.FloorToInt((minimumX + Cells - 1) / (float)ChunkCells), renderLastZ = Mathf.FloorToInt((minimumZ + Cells - 1) / (float)ChunkCells);
            for (int z = renderFirstZ; z <= renderLastZ; z++)
                for (int x = renderFirstX; x <= renderLastX; x++)
                    renderChunks[((z % RenderSide + RenderSide) % RenderSide) * RenderSide + (x % RenderSide + RenderSide) % RenderSide] = chunks[new Vector2Int(x, z)];
            for (int slot = 0; slot < renderChunks.Length; slot++)
                if (renderChunks[slot] != previousRenderChunks[slot]) contoursDirty = true;
            RestartMesh(false);
        }
        internal void QueueChunk(ProtectionChunk chunk)
        {
            if (!chunk.Queued && !chunk.Disposed) { chunk.Queued = true; work.Enqueue(chunk); }
        }
        internal void InvalidateDraw(ProtectionChunk chunk)
        {
            if (chunk.DrawCount == 0 && !IsRendered(chunk)) return;
            for (int offset = 0; offset < chunk.DrawCount; offset += zeroIndices.Length)
                GroundMesh.SetIndexBufferData(zeroIndices, 0, chunk.DrawStart + offset, Math.Min(zeroIndices.Length, chunk.DrawCount - offset), UploadFlags);
            chunk.DrawCount = 0;
            if (visibleBorderIndices > 0) { BorderMesh.SetSubMesh(0, new SubMeshDescriptor(0, 0), UploadFlags); visibleBorderIndices = 0; }
            RestartMesh();
        }
        private bool IsRendered(ProtectionChunk chunk)
        {
            foreach (var rendered in renderChunks) if (rendered == chunk) return true;
            return false;
        }
        internal void ChunkReady(ProtectionChunk chunk) { if (IsRendered(chunk)) RestartMesh(); }
        internal void RestartMesh(bool contoursChanged = true)
        {
            if (meshBuild != null) { meshBuild.Cancelled = true; meshBuild = null; }
            meshDirty = true; meshStarted = false; contoursDirty |= contoursChanged;
        }
        private void CaptureQueued(long deadline)
        {
            while (sourceChanges.Count > 0 && Stopwatch.GetTimestamp() < deadline)
            {
                var area = sourceChanges.Dequeue(); queuedSources.Remove(area); Capture(area);
            }
        }
        private void Process(long deadline)
        {
            CaptureQueued(deadline);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                if (geometryWorker.TryCompleted(out var job))
                {
                    if (job is ProtectionCoverage coverage) { coverage.Chunk.AcceptCoverage(coverage); continue; }
                    if (job is ProtectionTerrainBuild terrainBuild) { terrainBuild.Chunk.AcceptTerrain(terrainBuild); continue; }
                    var mesh = (ProtectionMeshBuild)job;
                    if (mesh != meshBuild || mesh.Cancelled) continue;
                    if (mesh.Error != null) throw new InvalidOperationException("Spawn protection mesh construction failed.", mesh.Error);
                    groundBuild = mesh.GroundVertices; groundIndexBuild = mesh.GroundIndices; borderBuild = mesh.BorderVertices;
                    drawRanges = mesh.DrawRanges; mergedBoundary = mesh.Boundary;
                    minimumHeight = mesh.MinimumHeight; maximumHeight = mesh.MaximumHeight;
                    meshBuild = null; contoursDirty = false; meshPhase = 4; uploadPosition = 0;
                }
                else if (work.Count > 0)
                {
                    var chunk = work.Dequeue(); chunk.Queued = false;
                    if (!chunk.Disposed && chunk.Step()) QueueChunk(chunk);
                }
                else if (geometryWorker.Pending > 0) break;
                else if (meshDirty) StepMesh();
                else break;
            }
        }

        private void StepMesh()
        {
            if (!meshStarted)
            {
                meshStarted = true;
                meshBuild = new ProtectionMeshBuild(renderChunks, terrain.ToArray(), minimumX, minimumZ, mergedBoundary, contoursDirty);
                geometryWorker.Enqueue(meshBuild);
                return;
            }
            if (meshPhase == 4)
            {
                EnsureMesh(groundBack, false, groundBuild.Count, groundIndexBuild.Count);
                EnsureMesh(borderBack, true, borderBuild.Count, borderBuild.Count / 4 * 6);
                EnsureArrays(); meshPhase++; uploadPosition = 0;
                return;
            }
            if (meshPhase == 5)
            {
                int vertices = Math.Min(2048, Math.Max(0, groundBuild.Count - uploadPosition));
                int indices = Math.Min(2048, Math.Max(0, groundIndexBuild.Count - uploadPosition));
                if (vertices > 0)
                {
                    groundBuild.CopyTo(uploadPosition, groundData, uploadPosition, vertices);
                    groundBack.SetVertexBufferData(groundData, uploadPosition, uploadPosition, vertices, 0, UploadFlags);
                }
                if (indices > 0)
                {
                    groundIndexBuild.CopyTo(uploadPosition, groundIndices, uploadPosition, indices);
                    groundBack.SetIndexBufferData(groundIndices, uploadPosition, uploadPosition, indices, UploadFlags);
                }
                uploadPosition += Math.Max(vertices, indices);
                if (uploadPosition == Math.Max(groundBuild.Count, groundIndexBuild.Count)) { meshPhase++; uploadPosition = 0; }
                return;
            }
            if (meshPhase == 6)
            {
                int count = Math.Min(2048, borderBuild.Count - uploadPosition);
                if (count > 0)
                {
                    borderBuild.CopyTo(uploadPosition, borderData, uploadPosition, count);
                    borderBack.SetVertexBufferData(borderData, uploadPosition, uploadPosition, count, 0, UploadFlags);
                    uploadPosition += count;
                }
                if (uploadPosition == borderBuild.Count) { meshPhase++; uploadPosition = 0; }
                return;
            }
            if (meshPhase == 7)
            {
                int total = borderBuild.Count / 4 * 6, count = Math.Min(4096, total - uploadPosition);
                if (count > 0) { borderBack.SetIndexBufferData(borderIndices, uploadPosition, uploadPosition, count, UploadFlags); uploadPosition += count; }
                if (uploadPosition == total) meshPhase++;
                return;
            }
            CommitMesh();
        }
        private void EnsureMesh(Mesh mesh, bool border, int vertices, int indices)
        {
            if (vertices > mesh.vertexCount) mesh.SetVertexBufferParams(Mathf.NextPowerOfTwo(Mathf.Max(64, vertices)), border ? BorderLayout : GroundLayout);
            indexCapacities.TryGetValue(mesh, out int capacity);
            if (indices > capacity)
            {
                capacity = Mathf.NextPowerOfTwo(Mathf.Max(64, indices));
                mesh.SetIndexBufferParams(capacity, IndexFormat.UInt32); indexCapacities[mesh] = capacity;
            }
        }
        private void EnsureArrays()
        {
            if (groundData.Length < groundBuild.Count) Array.Resize(ref groundData, Mathf.NextPowerOfTwo(groundBuild.Count));
            if (borderData.Length < borderBuild.Count) Array.Resize(ref borderData, Mathf.NextPowerOfTwo(borderBuild.Count));
            if (groundIndices.Length < groundIndexBuild.Count) Array.Resize(ref groundIndices, Mathf.NextPowerOfTwo(groundIndexBuild.Count));
            int borderCount = borderBuild.Count / 4 * 6;
            if (borderIndices.Length < borderCount)
            {
                int previous = borderIndices.Length / 6;
                Array.Resize(ref borderIndices, Mathf.NextPowerOfTwo(borderCount));
                for (int panel = previous; panel < borderIndices.Length / 6; panel++)
                {
                    int offset = panel * 6, vertex = panel * 4;
                    borderIndices[offset] = vertex; borderIndices[offset + 1] = vertex + 2; borderIndices[offset + 2] = vertex + 1;
                    borderIndices[offset + 3] = vertex + 1; borderIndices[offset + 4] = vertex + 2; borderIndices[offset + 5] = vertex + 3;
                }
            }
        }
        private void CommitMesh()
        {
            float low = minimumHeight, high = maximumHeight;
            if (float.IsInfinity(low)) low = high = cachedCenter.y;
            var bounds = new Bounds(new Vector3(minimumX + Reach, (low + high + BorderHeight) * 0.5f, minimumZ + Reach),
                new Vector3(Cells, high - low + BorderHeight + 1f, Cells));
            groundBack.bounds = borderBack.bounds = bounds;
            int borderCount = borderBuild.Count / 4 * 6;
            groundBack.SetSubMesh(0, new SubMeshDescriptor(0, groundIndexBuild.Count) { bounds = bounds, vertexCount = groundBuild.Count }, UploadFlags);
            borderBack.SetSubMesh(0, new SubMeshDescriptor(0, borderCount) { bounds = bounds, vertexCount = borderBuild.Count }, UploadFlags);
            var previous = GroundMesh; GroundMesh = groundBack; groundBack = previous;
            previous = BorderMesh; BorderMesh = borderBack; borderBack = previous;
            groundFilter.sharedMesh = GroundMesh; borderFilter.sharedMesh = BorderMesh;
            foreach (var chunk in chunks.Values) chunk.DrawCount = 0;
            foreach (var range in drawRanges) { range.Chunk.DrawStart = range.Start; range.Chunk.DrawCount = range.Count; }
            visibleBorderIndices = borderCount; meshDirty = false;
        }
    }

    [HarmonyPatch(typeof(EffectArea), "Awake")]
    internal static class SpawnProtectionAreaCreation { private static void Postfix(EffectArea __instance) => SpawnProtectionOverlay.AreaChanged(__instance); }
    [HarmonyPatch(typeof(EffectArea), "OnDestroy")]
    internal static class SpawnProtectionAreaRemoval { private static void Prefix(EffectArea __instance) => SpawnProtectionOverlay.AreaChanged(__instance); }
    [HarmonyPatch(typeof(EffectArea), "OnEnable")]
    internal static class SpawnProtectionAreaEnablement { private static void Postfix(EffectArea __instance) => SpawnProtectionOverlay.AreaChanged(__instance); }
    [HarmonyPatch(typeof(EffectArea), "OnDisable")]
    internal static class SpawnProtectionAreaDisablement { private static void Postfix(EffectArea __instance) => SpawnProtectionOverlay.AreaChanged(__instance); }
    [HarmonyPatch(typeof(CraftingStation), "GetExtensions")]
    internal static class SpawnProtectionStationRadius
    {
        private static void Prefix(float ___m_updateExtensionTimer, out bool __state) => __state = ___m_updateExtensionTimer >= 2f;
        private static void Postfix(CraftingStation __instance, bool __state)
        { if (__state) SpawnProtectionOverlay.ColliderChanged(__instance.m_effectAreaCollider); }
    }
    [HarmonyPatch(typeof(Heightmap), "RebuildCollisionMesh")]
    internal static class SpawnProtectionTerrainChanges { private static void Postfix(Heightmap __instance) => SpawnProtectionOverlay.TerrainChanged(__instance); }
    [HarmonyPatch(typeof(Heightmap), "OnDestroy")]
    internal static class SpawnProtectionTerrainRemoval { private static void Postfix(Heightmap __instance) => SpawnProtectionOverlay.TerrainChanged(__instance, true); }
    [HarmonyPatch(typeof(Heightmap), "set_IsDistantLod")]
    internal static class SpawnProtectionTerrainRole { private static void Postfix(Heightmap __instance) => SpawnProtectionOverlay.TerrainChanged(__instance); }
    [HarmonyPatch(typeof(Player), "CheckKeyboardRadialPressed")]
    internal static class SpawnProtectionShortcut
    {
        private static bool Prefix(Player __instance, ref bool __result)
        {
            if (__instance != Player.m_localPlayer || !SpawnProtectionOverlay.ShortcutHeld(__instance)) return true;
            __result = false; return false;
        }
    }
}
