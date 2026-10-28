using System.Runtime.CompilerServices;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

// Wave 7 tester (GPU-1): the renderer keeps the meshes of earlier batches and re-plans only what
// a new export invalidates (dirty chunks, plus meshes whose 27-chunk signature changed). After any
// sequence of edits that incremental state must equal a plan built from scratch.
public class Wave7StaticChunkIncrementalPlanTests
{
    private static readonly Render UnusedRender = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
    private static readonly Camera TestCamera = new(Vector3.Zero);

    private static readonly GeometryMaterialId[] Materials =
    [
        GeometryMaterialId.Stone, GeometryMaterialId.Dirt, GeometryMaterialId.Grass
    ];

    private enum SlotKind
    {
        Empty,
        Full,
        Subdivided
    }

    [Theory]
    [InlineData(7)]
    [InlineData(1234)]
    [InlineData(98765)]
    public void FiftyRandomEditsAcrossChunkBorders_IncrementalStateMatchesAFromScratchPlan(int seed)
    {
        Random random = new(seed);
        // Slots straddle the chunk borders at x = 16 and z = 16 (and y = 16 at the top).
        const int MinX = 12, MaxX = 20, MinY = 13, MaxY = 19, MinZ = 12, MaxZ = 20;
        Dictionary<(int X, int Y, int Z), (SlotKind Kind, GeometryMaterialId Material)> slots = new();
        for (int x = MinX; x < MaxX; x++)
        {
            for (int y = MinY; y < MaxY; y++)
            {
                for (int z = MinZ; z < MaxZ; z++)
                {
                    if (random.NextDouble() < 0.6)
                    {
                        slots[(x, y, z)] = (SlotKind.Full, Materials[random.Next(Materials.Length)]);
                    }
                }
            }
        }

        Dictionary<StaticWorldChunkMeshKey, (int Signature, StaticWorldChunkMesh Mesh)> state = new();
        ApplyIncremental(state, BuildCells(slots), dirtyChunks: []);
        AssertMatchesScratch(state, BuildCells(slots), step: 0);

        for (int step = 1; step <= 50; step++)
        {
            HashSet<StaticWorldChunkKey> dirty = [];
            int edits = 1 + random.Next(3);
            for (int e = 0; e < edits; e++)
            {
                // Bias edits onto the border planes, where the cross-chunk lookups live.
                int x = random.NextDouble() < 0.5 ? 15 + random.Next(2) : MinX + random.Next(MaxX - MinX);
                int y = MinY + random.Next(MaxY - MinY);
                int z = random.NextDouble() < 0.5 ? 15 + random.Next(2) : MinZ + random.Next(MaxZ - MinZ);
                double roll = random.NextDouble();
                if (roll < 0.4)
                {
                    slots.Remove((x, y, z));
                }
                else if (roll < 0.8)
                {
                    slots[(x, y, z)] = (SlotKind.Full, Materials[random.Next(Materials.Length)]);
                }
                else
                {
                    slots[(x, y, z)] = (SlotKind.Subdivided, Materials[random.Next(Materials.Length)]);
                }

                dirty.Add(StaticWorldChunkKey.FromWorldPosition(new Vector3(x, y, z)));
            }

            IReadOnlyList<StaticVoxelRenderCell> cells = BuildCells(slots);
            ApplyIncremental(state, cells, dirty);
            AssertMatchesScratch(state, cells, step);
        }
    }

    // Under an edit on every frame, with builds that take longer than one frame, every completed
    // batch is already superseded by the queued request. DrainCompletedBuild then drops the batch
    // and starts the queued one, so no batch is ever uploaded while the edits continue.
    [Fact(Skip = "bug: StaticWorldChunkRenderer.DrainCompletedBuild drops every superseded batch, so continuous static edits starve uploads (wave 7 tester report)")]
    public void ContinuousEdits_WithBuildsSlowerThanAFrame_StillUploadSomething()
    {
        GeometryRenderExport export = CaptureStartupExport();
        List<(TaskCompletionSource<StaticWorldChunkBuildBatch> Build, StaticWorldChunkBuildRequest Request, int StartFrame)> builds = [];
        CountingMeshStore store = new();
        int currentFrame = 0;
        using StaticWorldChunkRenderer renderer = new(
            request =>
            {
                TaskCompletionSource<StaticWorldChunkBuildBatch> build = new();
                builds.Add((build, request, currentFrame));
                return build.Task;
            },
            new RenderFaultLog(_ => { }),
            store);

        for (currentFrame = 0; currentFrame < 60; currentFrame++)
        {
            // One static edit per frame. A build finishes at the end of the frame after the one
            // that started it, so every build spans one full frame boundary.
            renderer.Draw(UnusedRender, TestCamera, export with { StaticContentVersion = export.StaticContentVersion + currentFrame });
            foreach ((TaskCompletionSource<StaticWorldChunkBuildBatch> build, StaticWorldChunkBuildRequest request, int startFrame) in builds.ToArray())
            {
                if (!build.Task.IsCompleted && startFrame < currentFrame)
                {
                    build.SetResult(StaticWorldChunkRenderer.BuildBatch(request));
                }
            }
        }

        int completed = builds.Count(entry => entry.Build.Task.IsCompleted);
        Assert.True(completed > 10, $"{completed} builds completed");
        Assert.True(store.UploadCount > 0, $"{completed} builds completed over 60 edited frames and none was uploaded.");
    }

    [Fact]
    public void ContinuousEdits_UploadOnceTheEditsStop()
    {
        // The other side of the starvation: as soon as a frame repeats its export, the pipeline drains.
        GeometryRenderExport export = CaptureStartupExport();
        List<TaskCompletionSource<StaticWorldChunkBuildBatch>> builds = [];
        List<StaticWorldChunkBuildRequest> requests = [];
        CountingMeshStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request =>
            {
                TaskCompletionSource<StaticWorldChunkBuildBatch> build = new();
                builds.Add(build);
                requests.Add(request);
                return build.Task;
            },
            new RenderFaultLog(_ => { }),
            store);

        GeometryRenderExport last = export;
        for (int frame = 0; frame < 80; frame++)
        {
            if (frame < 20)
            {
                last = export with { StaticContentVersion = export.StaticContentVersion + frame };
            }

            renderer.Draw(UnusedRender, TestCamera, last);
            for (int i = 0; i < builds.Count; i++)
            {
                if (!builds[i].Task.IsCompleted)
                {
                    builds[i].SetResult(StaticWorldChunkRenderer.BuildBatch(requests[i]));
                }
            }
        }

        Assert.True(store.UploadCount > 0);
        Assert.Equal(StaticWorldChunkRenderer.CalculateExportSignature(last), requests[^1].ExportSignature);
        Assert.True(renderer.Draw(UnusedRender, TestCamera, last).MeshCount > 0);
    }

    private static void ApplyIncremental(
        Dictionary<StaticWorldChunkMeshKey, (int Signature, StaticWorldChunkMesh Mesh)> state,
        IReadOnlyList<StaticVoxelRenderCell> cells,
        HashSet<StaticWorldChunkKey> dirtyChunks)
    {
        Dictionary<StaticWorldChunkMeshKey, int> cached = state.ToDictionary(pair => pair.Key, pair => pair.Value.Signature);
        IReadOnlyList<StaticWorldChunkBuildInput> inputs = StaticWorldChunkRenderer.PlanBuildInputs(cells, cached, dirtyChunks);
        HashSet<StaticWorldChunkMeshKey> replaced = [];
        foreach (StaticWorldChunkBuildInput input in inputs)
        {
            state[input.Key] = (input.Signature, input.BuildMesh());
            replaced.Add(input.Key);
        }

        // As the renderer does once a batch is fully uploaded: stale meshes of dirty chunks go.
        foreach (StaticWorldChunkMeshKey stale in state.Keys
            .Where(key => dirtyChunks.Contains(key.ChunkKey) && !replaced.Contains(key))
            .ToArray())
        {
            state.Remove(stale);
        }
    }

    private static void AssertMatchesScratch(
        Dictionary<StaticWorldChunkMeshKey, (int Signature, StaticWorldChunkMesh Mesh)> state,
        IReadOnlyList<StaticVoxelRenderCell> cells,
        int step)
    {
        Dictionary<StaticWorldChunkMeshKey, (int Signature, StaticWorldChunkMesh Mesh)> scratch = StaticWorldChunkRenderer
            .PlanBuildInputs(cells)
            .ToDictionary(input => input.Key, input => (input.Signature, input.BuildMesh()));

        foreach ((StaticWorldChunkMeshKey key, (int signature, StaticWorldChunkMesh mesh)) in scratch)
        {
            if (mesh.ExposedFaceCount == 0 && !state.ContainsKey(key))
            {
                continue;
            }

            Assert.True(state.TryGetValue(key, out (int Signature, StaticWorldChunkMesh Mesh) kept), $"step {step}: {key} missing");
            Assert.True(signature == kept.Signature, $"step {step}: {key} signature {kept.Signature} != scratch {signature}");
            Assert.True(mesh.ExposedFaceCount == kept.Mesh.ExposedFaceCount,
                $"step {step}: {key} faces {kept.Mesh.ExposedFaceCount} != scratch {mesh.ExposedFaceCount}");
            Assert.True(mesh.Vertices.SequenceEqual(kept.Mesh.Vertices), $"step {step}: {key} vertices differ");
            Assert.True(mesh.Indices.SequenceEqual(kept.Mesh.Indices), $"step {step}: {key} indices differ");
        }

        foreach ((StaticWorldChunkMeshKey key, (int _, StaticWorldChunkMesh mesh)) in state)
        {
            Assert.True(
                scratch.ContainsKey(key) || mesh.ExposedFaceCount == 0,
                $"step {step}: {key} kept with {mesh.ExposedFaceCount} faces but absent from the scratch plan");
        }
    }

    private static IReadOnlyList<StaticVoxelRenderCell> BuildCells(
        Dictionary<(int X, int Y, int Z), (SlotKind Kind, GeometryMaterialId Material)> slots)
    {
        List<StaticVoxelRenderCell> cells = [];
        foreach (((int x, int y, int z), (SlotKind kind, GeometryMaterialId material)) in slots
            .OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.Z))
        {
            if (kind == SlotKind.Full)
            {
                cells.Add(new StaticVoxelRenderCell(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), Vector3.One, material));
                continue;
            }

            for (int i = 0; i < 8; i++)
            {
                Vector3 center = new(x + 0.25f + (0.5f * (i & 1)), y + 0.25f + (0.5f * ((i >> 1) & 1)), z + 0.25f + (0.5f * (i >> 2)));
                cells.Add(new StaticVoxelRenderCell(center, new Vector3(0.5f), material));
            }
        }

        return cells;
    }

    private static GeometryRenderExport CaptureStartupExport()
    {
        Map map = new(
            physics: null,
            generateStartupTerrain: true,
            width: WorldDefaults.StartupFloorSize,
            depth: WorldDefaults.StartupFloorSize);
        GeometryRenderExport export = map.CapturePresentationGeometryRenderExport(new Vector3(12f, 2f, 12f), 16f);
        Assert.NotEqual(0, export.StaticContentVersion);
        return export;
    }

    private sealed class CountingMeshStore : IStaticWorldChunkMeshStore
    {
        private readonly HashSet<StaticWorldChunkMeshKey> _live = [];

        public int UploadCount { get; private set; }

        public void Upload(StaticWorldChunkMeshKey key, StaticWorldChunkMesh mesh)
        {
            UploadCount++;
            _live.Add(key);
        }

        public void Remove(StaticWorldChunkMeshKey key)
        {
            _live.Remove(key);
        }

        public bool Draw(Render render, Camera camera, in StaticWorldChunkMeshInfo mesh)
        {
            return _live.Contains(mesh.Key);
        }

        public void Clear()
        {
            _live.Clear();
        }
    }
}
