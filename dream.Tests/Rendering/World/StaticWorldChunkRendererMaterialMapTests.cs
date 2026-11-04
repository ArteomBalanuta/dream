using System.Runtime.CompilerServices;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Rendering.Core;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

// The renderer keeps the material map in step with the extracted export and hands it to the store
// once per change, before the pass that draws.
public class StaticWorldChunkRendererMaterialMapTests
{
    private static readonly Render UnusedRender = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));

    [Fact]
    public void SyncsOnceAfterTheFirstBuild_NotOnUnchangedFrames_AndOnceAfterAnEdit()
    {
        MapStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
        Camera camera = new(new Vector3(3f, 2f, 3f));
        camera.SetFacing(new Vector3(-1f, -0.5f, -1f));
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), ViewFrustumTests.GameProjection());

        Dictionary<(int, int, int), GeometryMaterialId> slots = new()
        {
            [(0, 0, 0)] = GeometryMaterialId.Grass,
            [(1, 0, 0)] = GeometryMaterialId.Stone
        };
        GeometryRenderExport export = CreateExport(slots, 1, []);
        for (int i = 0; i < 6; i++)
        {
            renderer.Draw(UnusedRender, camera, in frustum, export);
        }

        Assert.True(store.Drawn > 0, "no mesh was drawn, so no pass ran");
        Assert.Equal(1, store.SyncCount);
        Assert.Equal(new Vector3i(2, 1, 1), store.LastSize);
        Assert.Equal(1, store.LastTexels[0]);
        Assert.Equal(3, store.LastTexels[1]);

        slots[(0, 1, 0)] = GeometryMaterialId.Dirt;
        export = CreateExport(slots, 2, [new GeometryCellKey(0, 1, 0)]);
        for (int i = 0; i < 6; i++)
        {
            renderer.Draw(UnusedRender, camera, in frustum, export);
        }

        Assert.Equal(2, store.SyncCount);
        Assert.Equal(new Vector3i(2, 2, 1), store.LastSize);
        Assert.Equal(TerrainMaterialMap.CoveredTexel, store.LastTexels[0]);

        slots.Remove((0, 1, 0));
        export = CreateExport(slots, 3, [new GeometryCellKey(0, 1, 0)]);
        for (int i = 0; i < 6; i++)
        {
            renderer.Draw(UnusedRender, camera, in frustum, export);
        }

        Assert.Equal(3, store.SyncCount);
        Assert.Equal(new Vector3i(2, 1, 1), store.LastSize);
    }

    [Fact]
    public void ASupersededBatchActivatesItsOwnMap_ThenTheNewerBatchActivatesTheNewerMap()
    {
        MapStore store = new();
        List<TaskCompletionSource<StaticWorldChunkBuildBatch>> sources = [];
        List<StaticWorldChunkBuildRequest> requests = [];
        using StaticWorldChunkRenderer renderer = new(
            request =>
            {
                requests.Add(request);
                TaskCompletionSource<StaticWorldChunkBuildBatch> source = new();
                sources.Add(source);
                return source.Task;
            },
            new RenderFaultLog(_ => { }),
            store);
        Camera camera = new(new Vector3(3f, 2f, 3f));
        camera.SetFacing(new Vector3(-1f, -0.5f, -1f));
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), ViewFrustumTests.GameProjection());

        Dictionary<(int, int, int), GeometryMaterialId> slots = new() { [(0, 0, 0)] = GeometryMaterialId.Grass };
        for (int i = 0; i < 3; i++)
        {
            renderer.Draw(UnusedRender, camera, in frustum, CreateExport(slots, 1, []));
        }

        // The first build is in flight: no batch exists, so no map is pending and none is active.
        Assert.False(renderer.HasPendingTerrainMaterialMap);
        Assert.True(renderer.TerrainMaterialMap.IsEmpty);
        int generation = renderer.TerrainMaterialMapGeneration;
        Assert.Equal(0, store.SyncCount);

        // A newer export supersedes while the first build still runs; it only queues.
        slots[(1, 0, 0)] = GeometryMaterialId.Stone;
        slots[(2, 0, 0)] = GeometryMaterialId.Sand;
        GeometryRenderExport second = CreateExport(slots, 2, [new GeometryCellKey(1, 0, 0), new GeometryCellKey(2, 0, 0)]);
        for (int i = 0; i < 3; i++)
        {
            renderer.Draw(UnusedRender, camera, in frustum, second);
        }

        Assert.Equal(generation, renderer.TerrainMaterialMapGeneration);
        Assert.True(renderer.TerrainMaterialMap.IsEmpty);
        Assert.Single(sources);

        // The older batch applies while the newer request is in flight: the active map is the older
        // batch's (one cell), not the newer request's.
        sources[0].SetResult(StaticWorldChunkRenderer.BuildBatch(requests[0]));
        renderer.Draw(UnusedRender, camera, in frustum, second);
        Assert.Equal(2, sources.Count);
        Assert.False(renderer.HasPendingTerrainMaterialMap);
        Assert.Equal(generation + 1, renderer.TerrainMaterialMapGeneration);
        Assert.Equal(new Vector3i(1, 1, 1), renderer.TerrainMaterialMap.Size);
        generation = renderer.TerrainMaterialMapGeneration;

        // The newer batch applies: its map becomes active, once.
        sources[1].SetResult(StaticWorldChunkRenderer.BuildBatch(requests[1]));
        renderer.Draw(UnusedRender, camera, in frustum, second);
        Assert.False(renderer.HasPendingTerrainMaterialMap);
        Assert.Equal(generation + 1, renderer.TerrainMaterialMapGeneration);
        Assert.Equal(new Vector3i(3, 1, 1), renderer.TerrainMaterialMap.Size);
        generation = renderer.TerrainMaterialMapGeneration;

        // A further edit leaves the active map alone until its batch applies.
        slots[(0, 1, 0)] = GeometryMaterialId.Dirt;
        for (int i = 0; i < 3; i++)
        {
            renderer.Draw(UnusedRender, camera, in frustum, CreateExport(slots, 3, [new GeometryCellKey(0, 1, 0)]));
        }

        Assert.Equal(generation, renderer.TerrainMaterialMapGeneration);
        Assert.Equal(new Vector3i(3, 1, 1), renderer.TerrainMaterialMap.Size);

        // Drive every started build to completion.
        for (int round = 0; round < 8; round++)
        {
            for (int s = 0; s < sources.Count; s++)
            {
                if (!sources[s].Task.IsCompleted)
                {
                    sources[s].SetResult(StaticWorldChunkRenderer.BuildBatch(requests[s]));
                }
            }

            renderer.Draw(UnusedRender, camera, in frustum, CreateExport(slots, 3, []));
        }

        Assert.False(renderer.HasPendingTerrainMaterialMap);
        Assert.Equal(new Vector3i(3, 2, 1), renderer.TerrainMaterialMap.Size);
    }

    [Fact]
    public void AContinuousEditStream_StillActivatesTheMap_AlwaysMatchingAnAppliedBatch()
    {
        MapStore store = new();
        const int BuildFrames = 3;
        List<TaskCompletionSource<StaticWorldChunkBuildBatch>> sources = [];
        List<StaticWorldChunkBuildRequest> requests = [];
        List<int> startFrames = [];
        int frame = 0;
        using StaticWorldChunkRenderer renderer = new(
            request =>
            {
                requests.Add(request);
                startFrames.Add(frame);
                TaskCompletionSource<StaticWorldChunkBuildBatch> source = new();
                sources.Add(source);
                return source.Task;
            },
            new RenderFaultLog(_ => { }),
            store);
        Camera camera = new(new Vector3(3f, 2f, 3f));
        camera.SetFacing(new Vector3(-1f, -0.5f, -1f));
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), ViewFrustumTests.GameProjection());

        // A new column of cells every frame, while each build takes several frames. The cell count of
        // a request is its map's X size, so the active map identifies the batch it came from.
        Dictionary<(int, int, int), GeometryMaterialId> slots = [];
        HashSet<int> completedWidths = [];
        int lastWidth = 0;
        int activations = 0;
        int lastGeneration = renderer.TerrainMaterialMapGeneration;
        for (frame = 1; frame <= 40; frame++)
        {
            slots[(frame - 1, 0, 0)] = GeometryMaterialId.Grass;
            GeometryRenderExport export = CreateExport(slots, frame, [new GeometryCellKey(frame - 1, 0, 0)]);
            for (int s = 0; s < sources.Count; s++)
            {
                if (!sources[s].Task.IsCompleted && frame - startFrames[s] >= BuildFrames)
                {
                    completedWidths.Add(requests[s].Cells.Count);
                    sources[s].SetResult(StaticWorldChunkRenderer.BuildBatch(requests[s]));
                }
            }

            renderer.Draw(UnusedRender, camera, in frustum, export);
            if (renderer.TerrainMaterialMapGeneration != lastGeneration)
            {
                activations++;
                lastGeneration = renderer.TerrainMaterialMapGeneration;
            }

            int width = renderer.TerrainMaterialMap.Size.X;
            Assert.False(renderer.HasPendingTerrainMaterialMap);
            Assert.True(width == 0 || completedWidths.Contains(width), $"frame {frame}: active map width {width} is no completed batch");
            Assert.True(width >= lastWidth, $"frame {frame}: the active map went back from {lastWidth} to {width}");
            lastWidth = width;
        }

        Assert.True(activations >= 5, $"the map activated only {activations} times during the edit stream");
        Assert.True(lastWidth > 0);

        // Edits stop: the last batch applies and its map is the newest one.
        GeometryRenderExport last = CreateExport(slots, 40, []);
        for (int round = 0; round < 20; round++)
        {
            for (int s = 0; s < sources.Count; s++)
            {
                if (!sources[s].Task.IsCompleted)
                {
                    sources[s].SetResult(StaticWorldChunkRenderer.BuildBatch(requests[s]));
                }
            }

            renderer.Draw(UnusedRender, camera, in frustum, last);
        }

        Assert.Equal(new Vector3i(40, 1, 1), renderer.TerrainMaterialMap.Size);
    }

    [Fact]
    public void AFailedBuild_NeverSwapsTheMap_UntilTheRetryApplies()
    {
        MapStore store = new();
        int calls = 0;
        using StaticWorldChunkRenderer renderer = new(
            request => ++calls == 1
                ? Task.FromException<StaticWorldChunkBuildBatch>(new InvalidOperationException("build failed"))
                : Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
        Camera camera = new(new Vector3(3f, 2f, 3f));
        camera.SetFacing(new Vector3(-1f, -0.5f, -1f));
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), ViewFrustumTests.GameProjection());
        GeometryRenderExport export = CreateExport(new() { [(0, 0, 0)] = GeometryMaterialId.Grass }, 1, []);

        renderer.Draw(UnusedRender, camera, in frustum, export);
        renderer.Draw(UnusedRender, camera, in frustum, export);

        // The only build failed: no batch exists, so the active map is still the empty initial one
        // and nothing is pending until the retry's batch is taken.
        Assert.Equal(1, calls);
        Assert.False(renderer.HasPendingTerrainMaterialMap);
        Assert.True(renderer.TerrainMaterialMap.IsEmpty);
        Assert.Equal(0, store.SyncCount);

        for (int i = 0; i < 40 && renderer.TerrainMaterialMap.IsEmpty; i++)
        {
            renderer.Draw(UnusedRender, camera, in frustum, export);
        }

        Assert.True(calls >= 2, "the failed build was never retried");
        Assert.False(renderer.HasPendingTerrainMaterialMap);
        Assert.Equal(new Vector3i(1, 1, 1), renderer.TerrainMaterialMap.Size);
        renderer.Draw(UnusedRender, camera, in frustum, export);
        Assert.Equal(1, store.SyncCount);
    }

    [Fact]
    public void ASupersededRequestThatFaults_KeepsTheAppliedBatchesMap_UntilTheRetryApplies()
    {
        MapStore store = new();
        List<TaskCompletionSource<StaticWorldChunkBuildBatch>> sources = [];
        List<StaticWorldChunkBuildRequest> requests = [];
        using StaticWorldChunkRenderer renderer = new(
            request =>
            {
                requests.Add(request);
                TaskCompletionSource<StaticWorldChunkBuildBatch> source = new();
                sources.Add(source);
                return source.Task;
            },
            new RenderFaultLog(_ => { }),
            store);
        Camera camera = new(new Vector3(3f, 2f, 3f));
        camera.SetFacing(new Vector3(-1f, -0.5f, -1f));
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), ViewFrustumTests.GameProjection());

        // Request A (a single cell) is building; request B (three cells) supersedes it.
        Dictionary<(int, int, int), GeometryMaterialId> slots = new() { [(0, 0, 0)] = GeometryMaterialId.Grass };
        renderer.Draw(UnusedRender, camera, in frustum, CreateExport(slots, 1, []));
        slots[(1, 0, 0)] = GeometryMaterialId.Stone;
        slots[(2, 0, 0)] = GeometryMaterialId.Sand;
        GeometryRenderExport exportB = CreateExport(slots, 2, [new GeometryCellKey(1, 0, 0), new GeometryCellKey(2, 0, 0)]);
        renderer.Draw(UnusedRender, camera, in frustum, exportB);

        // A applies and B starts building; B then faults.
        sources[0].SetResult(StaticWorldChunkRenderer.BuildBatch(requests[0]));
        renderer.Draw(UnusedRender, camera, in frustum, exportB);
        Assert.Equal(2, sources.Count);
        Assert.True(store.Contains(store.LastUploaded!.Value), "A's meshes were not applied");
        sources[1].SetException(new InvalidOperationException("B failed"));
        renderer.Draw(UnusedRender, camera, in frustum, exportB);

        // Only A's meshes are in: the active map stays A's (one cell) and does not describe B.
        Assert.False(renderer.HasPendingTerrainMaterialMap);
        Assert.Equal(new Vector3i(1, 1, 1), renderer.TerrainMaterialMap.Size);
        int generationAfterA = renderer.TerrainMaterialMapGeneration;

        // The retry builds B again and only its own batch activates the map.
        for (int i = 0; i < 600 && renderer.TerrainMaterialMap.Size.X != 3; i++)
        {
            for (int s = 2; s < sources.Count; s++)
            {
                if (!sources[s].Task.IsCompleted)
                {
                    sources[s].SetResult(StaticWorldChunkRenderer.BuildBatch(requests[s]));
                }
            }

            renderer.Draw(UnusedRender, camera, in frustum, exportB);
        }

        Assert.Equal(new Vector3i(3, 1, 1), renderer.TerrainMaterialMap.Size);
        Assert.Equal(generationAfterA + 1, renderer.TerrainMaterialMapGeneration);
        Assert.False(renderer.HasPendingTerrainMaterialMap);
    }

    [Fact]
    public void FramesThatDrawNothing_DoNotSync_AndTheNextDrawingFrameSyncsOnce()
    {
        MapStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
        Camera away = new(new Vector3(3f, 2f, 3f));
        away.SetFacing(new Vector3(1f, 0.5f, 1f));
        ViewFrustum awayFrustum = ViewFrustum.FromViewAndProjection(away.GetViewMatrix(), ViewFrustumTests.GameProjection());
        Camera toward = new(new Vector3(3f, 2f, 3f));
        toward.SetFacing(new Vector3(-1f, -0.5f, -1f));
        ViewFrustum towardFrustum = ViewFrustum.FromViewAndProjection(toward.GetViewMatrix(), ViewFrustumTests.GameProjection());
        GeometryRenderExport export = CreateExport(new() { [(0, 0, 0)] = GeometryMaterialId.Grass }, 1, []);

        for (int i = 0; i < 6; i++)
        {
            renderer.Draw(UnusedRender, away, in awayFrustum, export);
        }

        Assert.Equal(0, store.Drawn);
        Assert.Equal(0, store.SyncCount);

        for (int i = 0; i < 6; i++)
        {
            renderer.Draw(UnusedRender, toward, in towardFrustum, export);
        }

        Assert.True(store.Drawn > 0);
        Assert.Equal(1, store.SyncCount);
        Assert.Equal(new Vector3i(1, 1, 1), store.LastSize);
    }

    [Fact]
    public void Dispose_ClearsTheMap()
    {
        MapStore store = new();
        StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
        Camera camera = new(new Vector3(3f, 2f, 3f));
        camera.SetFacing(new Vector3(-1f, -0.5f, -1f));
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), ViewFrustumTests.GameProjection());
        GeometryRenderExport export = CreateExport(new() { [(0, 0, 0)] = GeometryMaterialId.Grass }, 1, []);
        for (int i = 0; i < 4; i++)
        {
            renderer.Draw(UnusedRender, camera, in frustum, export);
        }

        Assert.False(renderer.TerrainMaterialMap.IsEmpty);

        renderer.Dispose();

        Assert.True(renderer.TerrainMaterialMap.IsEmpty);
        Assert.Equal(Vector3i.Zero, renderer.TerrainMaterialMap.Size);
    }

    private static GeometryRenderExport CreateExport(
        Dictionary<(int X, int Y, int Z), GeometryMaterialId> slots,
        long version,
        IReadOnlyList<GeometryCellKey> dirtyCells)
    {
        List<Cube> cubes = [];
        foreach (((int x, int y, int z), GeometryMaterialId material) in slots
            .OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.Z))
        {
            cubes.Add(new Cube(isEmpty: false, isSolid: true)
            {
                Position = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
                MaterialId = material,
                ShaderName = "default"
            });
        }

        return new GeometryRenderExport(
            DirtyCells: dirtyCells,
            Cells: [],
            Blocks: [],
            CompatibilityCubes: cubes)
        {
            StaticContentVersion = version
        };
    }

    private sealed class MapStore : IStaticWorldChunkMeshStore
    {
        private readonly HashSet<StaticWorldChunkMeshKey> _keys = new();

        public int SyncCount { get; private set; }

        public int Drawn { get; private set; }

        public Vector3i LastSize { get; private set; }

        public byte[] LastTexels { get; private set; } = [];

        public void SyncMaterialMap(TerrainMaterialMap map)
        {
            SyncCount++;
            LastSize = map.Size;
            LastTexels = map.Texels.AsSpan(0, map.Count).ToArray();
        }

        public StaticWorldChunkMeshKey? LastUploaded { get; private set; }

        public void Upload(StaticWorldChunkMeshKey key, StaticWorldChunkMesh mesh)
        {
            LastUploaded = key;
            _keys.Add(key);
        }

        public void Remove(StaticWorldChunkMeshKey key) => _keys.Remove(key);

        public bool Contains(StaticWorldChunkMeshKey key) => _keys.Contains(key);

        public void BeginPass(Render render, Camera camera)
        {
        }

        public void BindMaterial(Render render, int materialSlot)
        {
        }

        public void Draw(Render render, in StaticWorldChunkMeshInfo mesh) => Drawn++;

        public void EndPass(Render render)
        {
        }

        public void Clear() => _keys.Clear();
    }
}
