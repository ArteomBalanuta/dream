using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

// GPU-1: planning runs on the build worker, uploads are budgeted by bytes, and only mesh metadata
// stays on the CPU after upload. A recording mesh store stands in for GL.
public class StaticWorldChunkRendererUploadPipelineTests
{
    private static readonly Render UnusedRender = (Render)RuntimeHelpers.GetUninitializedObject(typeof(Render));
    private static readonly Camera TestCamera = new(Vector3.Zero);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Draw_HandsCellsToTheBuildTask_AndPlanningRunsInsideTheTask()
    {
        GeometryRenderExport export = CaptureStartupExport();
        int renderThread = Environment.CurrentManagedThreadId;
        StaticWorldChunkBuildRequest? captured = null;
        ThreadRecordingCells? recordingCells = null;
        Task<StaticWorldChunkBuildBatch>? buildTask = null;
        using StaticWorldChunkRenderer renderer = new(
            request =>
            {
                captured = request;
                // The request reaches the task unplanned; reads of its cells are recorded by thread.
                recordingCells = new ThreadRecordingCells(request.Cells);
                StaticWorldChunkBuildRequest recorded = request with { Cells = recordingCells };
                buildTask = Task.Run(() => StaticWorldChunkRenderer.BuildBatch(recorded));
                return buildTask;
            },
            new RenderFaultLog(_ => { }),
            new RecordingMeshStore());

        renderer.Draw(UnusedRender, TestCamera, export);

        Assert.NotNull(captured);
        Assert.NotNull(buildTask);
        Assert.NotNull(recordingCells);
        // Draw read no cell of the request on the render thread: no planning happened there.
        Assert.Equal(0, recordingCells.ReadsOnThread(renderThread));
        Assert.Equal(StaticWorldChunkExtractor.ExtractCells(export), captured.Cells);
        Assert.Empty(captured.CachedSignatures);
        // The request type carries cells, not planned inputs.
        Assert.DoesNotContain(
            typeof(StaticWorldChunkBuildRequest).GetProperties(),
            property => property.PropertyType == typeof(IReadOnlyList<StaticWorldChunkBuildInput>));

        Assert.True(((IAsyncResult)buildTask).AsyncWaitHandle.WaitOne(BuildTimeout));
        Assert.True(buildTask.IsCompletedSuccessfully, buildTask.Exception?.ToString());
        Assert.NotEmpty(buildTask.Result.Outputs);
        Assert.True(recordingCells.TotalReads > 0);
        Assert.Equal(0, recordingCells.ReadsOnThread(renderThread));
    }

    [Fact]
    public void SignatureSnapshot_IsACopy_NotTheLiveCache()
    {
        GeometryRenderExport first = CaptureStartupExport();
        List<StaticWorldChunkBuildRequest> requests = [];
        RecordingMeshStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request =>
            {
                requests.Add(request);
                return Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request));
            },
            new RenderFaultLog(_ => { }),
            store);

        DrawUntilApplied(renderer, first);
        IReadOnlyDictionary<StaticWorldChunkMeshKey, int> emptySnapshot = requests[0].CachedSignatures;
        Assert.Empty(emptySnapshot);

        GeometryRenderExport second = first with { StaticContentVersion = first.StaticContentVersion + 1 };
        renderer.Draw(UnusedRender, TestCamera, second);

        Assert.Equal(2, requests.Count);
        Assert.Empty(emptySnapshot);
        object liveSignatures = typeof(StaticWorldChunkRenderer)
            .GetField("_signatures", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        IReadOnlyDictionary<StaticWorldChunkMeshKey, int> snapshot = requests[1].CachedSignatures;
        Assert.NotSame(liveSignatures, snapshot);
        Assert.Equal((IReadOnlyDictionary<StaticWorldChunkMeshKey, int>)liveSignatures, snapshot);
        Assert.True(snapshot.Count >= store.LiveKeys.Count);
    }

    [Fact]
    public void UnchangedExportDraw_AllocatesNothing()
    {
        GeometryRenderExport export = CaptureStartupExport();
        RecordingMeshStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(StaticWorldChunkRenderer.BuildBatch(request)),
            new RenderFaultLog(_ => { }),
            store);
        DrawUntilApplied(renderer, export);
        Assert.NotEmpty(store.LiveKeys);

        long bytes = AllocationAssert.MeasureBytesPerIteration(() => renderer.Draw(UnusedRender, TestCamera, export));

        Assert.Equal(0, bytes);
        StaticWorldChunkRenderStats stats = renderer.Draw(UnusedRender, TestCamera, export);
        Assert.True(stats.MeshCount > 0);
        Assert.Equal(0, stats.RebuiltMeshes);
    }

    [Fact]
    public void LargeBatch_UploadsOverSeveralFrames_WithinTheByteBudget()
    {
        // 24 meshes of 400 separated cubes: 2400 faces each, about 365 KB per mesh, 8.8 MB in all.
        StaticWorldChunkBuildBatch batch = CreateLargeBatch(meshCount: 24, cellsPerMesh: 400, out long totalBytes);
        Assert.True(totalBytes > 3 * StaticWorldChunkRenderer.UploadByteBudgetPerFrame);
        RecordingMeshStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(batch with { ExportSignature = request.ExportSignature, DirtyChunks = request.DirtyChunks }),
            new RenderFaultLog(_ => { }),
            store);
        GeometryRenderExport export = CreateTinyExport();

        List<long> bytesPerFrame = [];
        int frames = 0;
        while (store.UploadCount < batch.Outputs.Count && frames < 100)
        {
            long before = store.UploadedBytes;
            renderer.Draw(UnusedRender, TestCamera, export);
            long uploaded = store.UploadedBytes - before;
            if (uploaded > 0)
            {
                bytesPerFrame.Add(uploaded);
            }

            frames++;
        }

        Assert.Equal(batch.Outputs.Count, store.UploadCount);
        Assert.Equal(totalBytes, store.UploadedBytes);
        Assert.True(bytesPerFrame.Count >= 4, $"uploaded in {bytesPerFrame.Count} frames");
        Assert.All(bytesPerFrame, bytes => Assert.InRange(bytes, 1, StaticWorldChunkRenderer.UploadByteBudgetPerFrame));
    }

    [Fact]
    public void MeshLargerThanTheBudget_StillUploadsOnePerFrame()
    {
        // 6000 separated cubes in one mesh: 36000 faces, about 5.5 MB.
        StaticWorldChunkBuildBatch batch = CreateLargeBatch(meshCount: 2, cellsPerMesh: 6000, out long totalBytes);
        Assert.True(totalBytes / 2 > StaticWorldChunkRenderer.UploadByteBudgetPerFrame);
        RecordingMeshStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(batch with { ExportSignature = request.ExportSignature, DirtyChunks = request.DirtyChunks }),
            new RenderFaultLog(_ => { }),
            store);
        GeometryRenderExport export = CreateTinyExport();

        renderer.Draw(UnusedRender, TestCamera, export);
        renderer.Draw(UnusedRender, TestCamera, export);
        Assert.Equal(1, store.UploadCount);
        renderer.Draw(UnusedRender, TestCamera, export);
        Assert.Equal(2, store.UploadCount);
    }

    [Fact]
    public void AfterUpload_NoVertexOrIndexListsAreRetained()
    {
        List<WeakReference> meshLists = [];
        RecordingMeshStore store = new();
        StaticWorldChunkRenderer renderer = CreateRendererThatBuildsOnce(store, meshLists);
        using (renderer)
        {
            GeometryRenderExport export = CaptureStartupExport();
            DrawUntilApplied(renderer, export);
            Assert.NotEmpty(store.LiveKeys);
            Assert.NotEmpty(meshLists);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.DoesNotContain(meshLists, reference => reference.IsAlive);
            StaticWorldChunkRenderStats stats = renderer.Draw(UnusedRender, TestCamera, export);
            Assert.True(stats.MeshCount > 0);
            Assert.True(stats.Triangles > 0);
        }
    }

    [Fact]
    public void RendererFields_HoldNoMeshGeometry()
    {
        // The per-mesh record keeps key, bounds and counts only.
        Assert.All(
            typeof(StaticWorldChunkMeshInfo).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field =>
            {
                Assert.False(typeof(IEnumerable).IsAssignableFrom(field.FieldType), field.Name);
                Assert.NotEqual(typeof(StaticWorldChunkMesh), field.FieldType);
            });

        foreach (FieldInfo field in typeof(StaticWorldChunkRenderer).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            Type type = field.FieldType;
            Assert.NotEqual(typeof(StaticWorldChunkMesh), type);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                Type valueType = type.GetGenericArguments()[1];
                Assert.NotEqual(typeof(StaticWorldChunkMesh), valueType);
                Assert.NotEqual(typeof(StaticMeshBuffer), valueType);
            }
        }
    }

    [Fact]
    public void Rebuild_ReusesTheExistingStoreEntry_AndEmptiedMeshesAreRemoved()
    {
        StaticVoxelRenderCell cell = new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Stone);
        StaticWorldChunkBuildInput input = Assert.Single(StaticWorldChunkRenderer.PlanBuildInputs([cell]));
        StaticWorldChunkBuildOutput built = new(input.Key, input.Signature, input.BuildMesh());
        StaticWorldChunkBuildOutput emptied = new(
            input.Key,
            input.Signature + 1,
            StaticWorldChunkMeshBuilder.Build(input.Key.ChunkKey, input.Key.MaterialId, input.Key.MaterialRole, [], []));
        Queue<StaticWorldChunkBuildOutput> outputs = new([built, built with { Signature = input.Signature + 2 }, emptied]);
        RecordingMeshStore store = new();
        using StaticWorldChunkRenderer renderer = new(
            request => Task.FromResult(new StaticWorldChunkBuildBatch(request.ExportSignature, request.DirtyChunks, [outputs.Dequeue()])),
            new RenderFaultLog(_ => { }),
            store);

        GeometryRenderExport export = CreateTinyExport();
        DrawUntilApplied(renderer, export);
        DrawUntilApplied(renderer, export with { StaticContentVersion = export.StaticContentVersion + 1 });
        Assert.Equal(2, store.UploadCount);
        Assert.Equal(1, store.CreatedCount);
        Assert.Single(store.LiveKeys);

        DrawUntilApplied(renderer, export with { StaticContentVersion = export.StaticContentVersion + 2 });
        Assert.Empty(store.LiveKeys);
        Assert.Equal(0, renderer.Draw(UnusedRender, TestCamera, export with { StaticContentVersion = export.StaticContentVersion + 2 }).MeshCount);
    }

    private static StaticWorldChunkRenderer CreateRendererThatBuildsOnce(RecordingMeshStore store, List<WeakReference> meshLists)
    {
        return new StaticWorldChunkRenderer(
            request => Task.FromResult(BuildAndTrack(request, meshLists)),
            new RenderFaultLog(_ => { }),
            store);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StaticWorldChunkBuildBatch BuildAndTrack(StaticWorldChunkBuildRequest request, List<WeakReference> meshLists)
    {
        StaticWorldChunkBuildBatch batch = StaticWorldChunkRenderer.BuildBatch(request);
        foreach (StaticWorldChunkBuildOutput output in batch.Outputs)
        {
            meshLists.Add(new WeakReference(output.Mesh.Vertices));
            meshLists.Add(new WeakReference(output.Mesh.Indices));
        }

        return batch;
    }

    private static void DrawUntilApplied(StaticWorldChunkRenderer renderer, GeometryRenderExport export)
    {
        for (int frame = 0; frame < 64; frame++)
        {
            StaticWorldChunkRenderStats stats = renderer.Draw(UnusedRender, TestCamera, export);
            if (frame > 0 && stats.RebuiltMeshes == 0)
            {
                return;
            }
        }

        Assert.Fail("The static chunk batch was not applied.");
    }

    private static StaticWorldChunkBuildBatch CreateLargeBatch(int meshCount, int cellsPerMesh, out long totalBytes)
    {
        List<StaticWorldChunkBuildOutput> outputs = [];
        totalBytes = 0;
        for (int m = 0; m < meshCount; m++)
        {
            StaticWorldChunkKey chunk = new(m, 0, 0);
            List<StaticVoxelRenderCell> cells = [];
            for (int i = 0; i < cellsPerMesh; i++)
            {
                // Two-unit spacing keeps every face of every cube exposed.
                cells.Add(new StaticVoxelRenderCell(
                    new Vector3((m * 16) + 0.5f + (2 * (i % 8)), 0.5f + (2 * (i / 64)), 0.5f + (2 * ((i / 8) % 8))),
                    Vector3.One,
                    GeometryMaterialId.Stone));
            }

            StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(chunk, GeometryMaterialId.Stone, cells);
            Assert.Equal(cellsPerMesh * 6, mesh.ExposedFaceCount);
            totalBytes += mesh.UploadByteCount;
            outputs.Add(new StaticWorldChunkBuildOutput(
                new StaticWorldChunkMeshKey(chunk, GeometryMaterialId.Stone, StaticTerrainMaterialRole.Single),
                m,
                mesh));
        }

        return new StaticWorldChunkBuildBatch(0, new HashSet<StaticWorldChunkKey>(), outputs);
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

    private static GeometryRenderExport CreateTinyExport()
    {
        Cube cube = new(isEmpty: false, isSolid: true)
        {
            Position = new Vector3(0.5f, 0.5f, 0.5f),
            MaterialId = GeometryMaterialId.Stone,
            ShaderName = "default"
        };

        return new GeometryRenderExport(
            DirtyCells: [],
            Cells: [],
            Blocks: [],
            CompatibilityCubes: [cube])
        {
            StaticContentVersion = 1
        };
    }

    private sealed class RecordingMeshStore : IStaticWorldChunkMeshStore
    {
        public HashSet<StaticWorldChunkMeshKey> LiveKeys { get; } = [];

        public int UploadCount { get; private set; }

        public int CreatedCount { get; private set; }

        public long UploadedBytes { get; private set; }

        public void Upload(StaticWorldChunkMeshKey key, StaticWorldChunkMesh mesh)
        {
            // Records counts only; the mesh itself is not kept.
            UploadCount++;
            UploadedBytes += mesh.UploadByteCount;
            if (LiveKeys.Add(key))
            {
                CreatedCount++;
            }
        }

        public void Remove(StaticWorldChunkMeshKey key)
        {
            LiveKeys.Remove(key);
        }

        public bool Contains(StaticWorldChunkMeshKey key)
        {
            return LiveKeys.Contains(key);
        }

        public void BeginPass(Render render, Camera camera)
        {
        }

        public void BindMaterial(Render render, int materialSlot)
        {
        }

        public void Draw(Render render, in StaticWorldChunkMeshInfo mesh)
        {
        }

        public void EndPass(Render render)
        {
        }

        public void Clear()
        {
            LiveKeys.Clear();
        }
    }

    private sealed class ThreadRecordingCells(IReadOnlyList<StaticVoxelRenderCell> inner) : IReadOnlyList<StaticVoxelRenderCell>
    {
        private readonly Dictionary<int, int> _readsByThread = new();
        private readonly object _gate = new();

        public int TotalReads
        {
            get
            {
                lock (_gate)
                {
                    return _readsByThread.Values.Sum();
                }
            }
        }

        public int Count
        {
            get
            {
                Record();
                return inner.Count;
            }
        }

        public StaticVoxelRenderCell this[int index]
        {
            get
            {
                Record();
                return inner[index];
            }
        }

        public int ReadsOnThread(int threadId)
        {
            lock (_gate)
            {
                return _readsByThread.GetValueOrDefault(threadId);
            }
        }

        public IEnumerator<StaticVoxelRenderCell> GetEnumerator()
        {
            Record();
            return inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private void Record()
        {
            lock (_gate)
            {
                int thread = Environment.CurrentManagedThreadId;
                _readsByThread[thread] = _readsByThread.GetValueOrDefault(thread) + 1;
            }
        }
    }
}
