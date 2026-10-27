using Dream.Rendering.Core;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using System.Diagnostics;

namespace Dream.Rendering.World;

public sealed class StaticWorldChunkRenderer : IDisposable
{
    public const string BuildFaultSource = "static-chunk-build";
    public const int BuildFaultBaseBackoffFrames = 15;
    public const int BuildFaultMaxBackoffFrames = 480;
    private const int MaxMeshUploadsPerFrame = 8;
    // Only used when the export is unchanged, where dirty chunks are neither planned nor scheduled.
    private static readonly HashSet<StaticWorldChunkKey> UnchangedExportDirtyChunks = [];
    private readonly Dictionary<StaticWorldChunkMeshKey, StaticMeshBuffer> _buffers = new();
    private readonly Dictionary<StaticWorldChunkMeshKey, StaticWorldChunkMesh> _meshes = new();
    private readonly Dictionary<StaticWorldChunkMeshKey, int> _signatures = new();
    private readonly Func<StaticWorldChunkBuildRequest, Task<StaticWorldChunkBuildBatch>> _startBuildTask;
    private readonly IRenderFaultSink _faultSink;
    private Task<StaticWorldChunkBuildBatch>? _activeBuildTask;
    private StaticWorldChunkBuildRequest? _activeBuildRequest;
    private StaticWorldChunkBuildRequest? _queuedBuildRequest;
    private StaticWorldChunkUploadBatch? _activeUploadBatch;
    // Dirty chunks of failed builds, folded into the next planned request so empty-chunk removals survive.
    private HashSet<StaticWorldChunkKey>? _retryDirtyChunks;
    private int _lastAppliedExportSignature;
    private int? _lastScheduledExportSignature;
    private int _consecutiveBuildFaults;
    private int _buildBackoffFramesRemaining;
    private bool _hasAppliedExport;
    private bool _disposed;

    public StaticWorldChunkRenderer(
        Func<StaticWorldChunkBuildRequest, Task<StaticWorldChunkBuildBatch>>? startBuildTask = null,
        IRenderFaultSink? faultSink = null)
    {
        _startBuildTask = startBuildTask ?? StartDefaultBuildTask;
        _faultSink = faultSink ?? RenderFaultLog.Shared;
    }

    public static StaticWorldChunkBuildBatch BuildBatch(StaticWorldChunkBuildRequest request)
    {
        return new StaticWorldChunkBuildBatch(
            request.ExportSignature,
            request.DirtyChunks,
            BuildOutputs(request.Inputs));
    }

    public static IReadOnlyList<StaticWorldChunkBuildOutput> BuildOutputs(IReadOnlyList<StaticWorldChunkBuildInput> inputs)
    {
        return inputs
            .Select(input => new StaticWorldChunkBuildOutput(
                input.Key,
                input.Signature,
                input.BuildMesh()))
            .ToArray();
    }

    public static IReadOnlyList<StaticWorldChunkMesh> PlanMeshes(IReadOnlyList<StaticVoxelRenderCell> cells)
    {
        return PlanBuildInputs(cells)
            .Select(input => input.BuildMesh())
            .Where(mesh => mesh.ExposedFaceCount > 0)
            .ToArray();
    }

    public static IReadOnlyList<StaticWorldChunkBuildInput> PlanBuildInputs(IReadOnlyList<StaticVoxelRenderCell> cells)
    {
        return PlanBuildInputs(
            cells,
            cachedSignatures: new Dictionary<StaticWorldChunkMeshKey, int>(),
            dirtyChunks: new HashSet<StaticWorldChunkKey>());
    }

    public static IReadOnlyList<StaticWorldChunkBuildInput> PlanBuildInputs(
        IReadOnlyList<StaticVoxelRenderCell> cells,
        IReadOnlyDictionary<StaticWorldChunkMeshKey, int> cachedSignatures,
        IReadOnlySet<StaticWorldChunkKey> dirtyChunks)
    {
        Dictionary<StaticWorldChunkKey, StaticVoxelRenderCell[]> cellsByChunk = cells
            .GroupBy(cell => StaticWorldChunkKey.FromWorldPosition(cell.Center))
            .ToDictionary(group => group.Key, group => group.ToArray());
        List<StaticWorldChunkBuildInput> inputs = new();

        foreach ((StaticWorldChunkKey chunkKey, StaticVoxelRenderCell[] chunkCells) in cellsByChunk)
        {
            StaticVoxelRenderCell[] occluderCells = CollectNeighborOccluders(chunkKey, cellsByChunk);
            foreach (IGrouping<GeometryMaterialId, StaticVoxelRenderCell> materialGroup in chunkCells.GroupBy(cell => cell.MaterialId))
            {
                StaticVoxelRenderCell[] materialCells = materialGroup.ToArray();
                foreach (StaticTerrainMaterialRole role in StaticTerrainMaterialCatalog.ResolveRoles(materialGroup.Key))
                {
                    StaticWorldChunkMeshKey meshKey = new(chunkKey, materialGroup.Key, role);
                    int signature = CalculateInputSignature(meshKey, materialCells, occluderCells);
                    if (!dirtyChunks.Contains(chunkKey) &&
                        cachedSignatures.TryGetValue(meshKey, out int cachedSignature) &&
                        cachedSignature == signature)
                    {
                        continue;
                    }

                    inputs.Add(new StaticWorldChunkBuildInput(
                        meshKey,
                        materialCells,
                        occluderCells,
                        signature));
                }
            }
        }

        return inputs;
    }

    public static bool IsChunkVisible(Vector3 cameraPosition, Vector3 cameraFront, StaticWorldChunkMesh mesh)
    {
        if (DistanceSquaredToBounds(cameraPosition, mesh.Bounds) <=
            WorldVisibility.MaxRenderDistance * WorldVisibility.MaxRenderDistance)
        {
            return true;
        }

        return WorldVisibility.IsPositionVisible(
            cameraPosition,
            cameraFront,
            mesh.Bounds.Center,
            mesh.Bounds.Size.Length * 0.5f);
    }

    public StaticWorldChunkRenderStats Draw(Render render, Camera camera, GeometryRenderExport export)
    {
        DrainCompletedBuild();
        Stopwatch stopwatch = Stopwatch.StartNew();
        int exportSignature = CalculateExportSignature(export);
        stopwatch.Stop();
        double signatureMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        bool exportUnchanged = _hasAppliedExport && exportSignature == _lastAppliedExportSignature;
        IReadOnlyList<StaticVoxelRenderCell> cells = exportUnchanged
            ? []
            : StaticWorldChunkExtractor.ExtractCells(export);
        stopwatch.Stop();
        double extractMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        HashSet<StaticWorldChunkKey> dirtyChunks = exportUnchanged
            ? UnchangedExportDirtyChunks
            : CollectDirtyChunks(export);
        IReadOnlyList<StaticWorldChunkBuildInput> inputs = exportUnchanged
            ? []
            : PlanBuildInputs(cells, _signatures, dirtyChunks);
        stopwatch.Stop();
        double planMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        bool buildBackoffActive = ConsumeBuildBackoffFrame();
        if (!exportUnchanged &&
            !buildBackoffActive &&
            exportSignature != _lastScheduledExportSignature)
        {
            ScheduleBuild(new StaticWorldChunkBuildRequest(exportSignature, dirtyChunks, inputs));
        }

        StaticWorldChunkSyncStats syncStats = ApplyPendingUploads();
        stopwatch.Stop();
        double syncMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        int visibleChunks = 0;
        int visibleMeshes = 0;
        int visibleFaces = 0;
        int visibleTriangles = 0;
        stopwatch.Restart();
        foreach (StaticWorldChunkMesh mesh in _meshes.Values)
        {
            if (!IsChunkVisible(camera.Position, camera.Front, mesh))
            {
                continue;
            }

            if (!_buffers.TryGetValue(new StaticWorldChunkMeshKey(mesh.Key, mesh.MaterialId, mesh.MaterialRole), out StaticMeshBuffer? buffer))
            {
                continue;
            }

            render.DrawStaticMesh(camera, buffer, mesh.MaterialId, mesh.MaterialRole);
            visibleChunks++;
            visibleMeshes++;
            visibleFaces += mesh.ExposedFaceCount;
            visibleTriangles += mesh.TriangleCount;
        }
        stopwatch.Stop();

        return new StaticWorldChunkRenderStats(
            visibleChunks,
            visibleMeshes,
            visibleFaces,
            visibleTriangles,
            signatureMilliseconds,
            extractMilliseconds,
            planMilliseconds,
            syncMilliseconds,
            stopwatch.Elapsed.TotalMilliseconds,
            syncStats.RebuiltMeshes,
            syncStats.ReusedMeshes);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        BackgroundWork.ObserveAndForget(_activeBuildTask, _faultSink, BuildFaultSource);
        _activeBuildTask = null;
        _activeBuildRequest = null;
        _queuedBuildRequest = null;
        _activeUploadBatch = null;
        _retryDirtyChunks = null;
        ClearBuffers();
        _disposed = true;
    }

    private void ClearBuffers()
    {
        foreach (StaticMeshBuffer buffer in _buffers.Values)
        {
            buffer.Dispose();
        }

        _buffers.Clear();
        _meshes.Clear();
        _signatures.Clear();
        _lastAppliedExportSignature = 0;
        _lastScheduledExportSignature = null;
        _consecutiveBuildFaults = 0;
        _buildBackoffFramesRemaining = 0;
        _hasAppliedExport = false;
    }

    private void RemoveMesh(StaticWorldChunkMeshKey key)
    {
        if (_buffers.Remove(key, out StaticMeshBuffer? buffer))
        {
            buffer.Dispose();
        }

        _meshes.Remove(key);
        _signatures.Remove(key);
    }

    private HashSet<StaticWorldChunkKey> CollectDirtyChunks(GeometryRenderExport export)
    {
        HashSet<StaticWorldChunkKey> dirtyChunks = export.StaticDirtyCells
            .Select(cell => StaticWorldChunkKey.FromWorldPosition(new Vector3(cell.X, cell.Y, cell.Z)))
            .ToHashSet();
        if (_retryDirtyChunks != null)
        {
            dirtyChunks.UnionWith(_retryDirtyChunks);
        }

        return dirtyChunks;
    }

    private bool ConsumeBuildBackoffFrame()
    {
        if (_buildBackoffFramesRemaining <= 0)
        {
            return false;
        }

        _buildBackoffFramesRemaining--;
        return true;
    }

    private void ScheduleBuild(StaticWorldChunkBuildRequest request)
    {
        // The request was planned with the retry chunks already folded into its dirty set.
        _retryDirtyChunks = null;
        _lastScheduledExportSignature = request.ExportSignature;
        if (_activeBuildTask == null)
        {
            StartBuild(request);
            return;
        }

        // Replacing a queued request used to drop its dirty chunks, and with them the removal of
        // meshes for chunks that became empty (a static ghost with no collision).
        _queuedBuildRequest = _queuedBuildRequest == null
            ? request
            : StaticWorldChunkBuildRequest.MergeSuperseded(_queuedBuildRequest, request);
    }

    private void DrainCompletedBuild()
    {
        // Finish uploading the current batch before taking the next one; overwriting a partially
        // applied batch skipped its remaining meshes and its stale-mesh removal.
        if (_activeUploadBatch != null || _activeBuildTask == null)
        {
            if (_activeBuildTask == null && _queuedBuildRequest != null)
            {
                StaticWorldChunkBuildRequest nextRequest = _queuedBuildRequest;
                _queuedBuildRequest = null;
                StartBuild(nextRequest);
            }

            return;
        }

        if (!BackgroundWork.TryTakeCompleted(
            ref _activeBuildTask,
            out StaticWorldChunkBuildBatch? completed,
            out Exception? fault))
        {
            if (fault != null)
            {
                HandleBuildFault(fault);
            }

            return;
        }

        _activeBuildRequest = null;
        if (completed == null)
        {
            HandleBuildFault(new InvalidOperationException("Static chunk build produced no batch."));
            return;
        }

        _consecutiveBuildFaults = 0;
        if (_queuedBuildRequest != null &&
            _queuedBuildRequest.ExportSignature != completed.ExportSignature)
        {
            // The completed batch is superseded; carry its dirty chunks so empty-chunk removals survive.
            StaticWorldChunkBuildRequest nextRequest = StaticWorldChunkBuildRequest.MergeDirtyChunks(
                _queuedBuildRequest,
                completed.DirtyChunks);
            _queuedBuildRequest = null;
            StartBuild(nextRequest);
            return;
        }

        _activeUploadBatch = new StaticWorldChunkUploadBatch(completed);
    }

    // Keeps every existing buffer and never creates an upload batch, so the last good world stays
    // visible and no stale-mesh removal runs for chunks that were not rebuilt.
    private void HandleBuildFault(Exception fault)
    {
        StaticWorldChunkBuildRequest? failedRequest = _activeBuildRequest;
        _activeBuildRequest = null;
        _faultSink.Report(BuildFaultSource, fault);

        HashSet<StaticWorldChunkKey> retryDirtyChunks = _retryDirtyChunks ?? [];
        if (failedRequest != null)
        {
            retryDirtyChunks.UnionWith(failedRequest.DirtyChunks);
        }

        // A queued request is re-planned from the next export anyway; only its dirty chunks matter.
        if (_queuedBuildRequest != null)
        {
            retryDirtyChunks.UnionWith(_queuedBuildRequest.DirtyChunks);
            _queuedBuildRequest = null;
        }

        _retryDirtyChunks = retryDirtyChunks;
        _lastScheduledExportSignature = null;
        _consecutiveBuildFaults++;
        _buildBackoffFramesRemaining = CalculateBuildBackoffFrames(_consecutiveBuildFaults);
    }

    public static int CalculateBuildBackoffFrames(int consecutiveFaults)
    {
        if (consecutiveFaults <= 0)
        {
            return 0;
        }

        int doublings = Math.Min(consecutiveFaults - 1, 30);
        long frames = (long)BuildFaultBaseBackoffFrames << doublings;
        return (int)Math.Min(frames, BuildFaultMaxBackoffFrames);
    }

    private StaticWorldChunkSyncStats ApplyPendingUploads()
    {
        if (_activeUploadBatch == null)
        {
            return new StaticWorldChunkSyncStats(0, _meshes.Count);
        }

        int rebuiltMeshes = 0;
        int reusedMeshes = 0;
        int uploadsRemaining = MaxMeshUploadsPerFrame;
        while (uploadsRemaining > 0 && _activeUploadBatch.TryTakeNext(out StaticWorldChunkBuildOutput? output))
        {
            ArgumentNullException.ThrowIfNull(output);
            ReplaceMesh(output);
            rebuiltMeshes++;
            uploadsRemaining--;
        }

        if (_activeUploadBatch.IsComplete)
        {
            foreach (StaticWorldChunkMeshKey staleKey in _signatures.Keys
                .Where(key => _activeUploadBatch.DirtyChunks.Contains(key.ChunkKey) &&
                    !_activeUploadBatch.ReplacedKeys.Contains(key))
                .ToArray())
            {
                RemoveMesh(staleKey);
            }

            _lastAppliedExportSignature = _activeUploadBatch.ExportSignature;
            _hasAppliedExport = true;
            reusedMeshes = _meshes.Count - rebuiltMeshes;
            _activeUploadBatch = null;
        }

        return new StaticWorldChunkSyncStats(rebuiltMeshes, Math.Max(0, reusedMeshes));
    }

    private void ReplaceMesh(StaticWorldChunkBuildOutput output)
    {
        if (_buffers.Remove(output.Key, out StaticMeshBuffer? oldBuffer))
        {
            oldBuffer.Dispose();
        }

        _meshes[output.Key] = output.Mesh;
        _signatures[output.Key] = output.Signature;

        if (output.Mesh.ExposedFaceCount > 0)
        {
            _buffers[output.Key] = new StaticMeshBuffer(output.Mesh);
        }
    }

    private void StartBuild(StaticWorldChunkBuildRequest request)
    {
        _activeBuildRequest = request;
        try
        {
            _activeBuildTask = _startBuildTask(request);
        }
        catch (Exception ex)
        {
            _activeBuildTask = Task.FromException<StaticWorldChunkBuildBatch>(ex);
        }
    }

    private static Task<StaticWorldChunkBuildBatch> StartDefaultBuildTask(StaticWorldChunkBuildRequest request)
    {
        return Task.Run(() => BuildBatch(request));
    }

    private static StaticVoxelRenderCell[] CollectNeighborOccluders(
        StaticWorldChunkKey chunkKey,
        IReadOnlyDictionary<StaticWorldChunkKey, StaticVoxelRenderCell[]> cellsByChunk)
    {
        List<StaticVoxelRenderCell> occluders = new();
        for (int x = chunkKey.X - 1; x <= chunkKey.X + 1; x++)
        {
            for (int y = chunkKey.Y - 1; y <= chunkKey.Y + 1; y++)
            {
                for (int z = chunkKey.Z - 1; z <= chunkKey.Z + 1; z++)
                {
                    if (cellsByChunk.TryGetValue(new StaticWorldChunkKey(x, y, z), out StaticVoxelRenderCell[]? chunkCells))
                    {
                        occluders.AddRange(chunkCells);
                    }
                }
            }
        }

        return occluders.ToArray();
    }

    private static int CalculateInputSignature(
        StaticWorldChunkMeshKey key,
        IReadOnlyList<StaticVoxelRenderCell> materialCells,
        IReadOnlyList<StaticVoxelRenderCell> occluderCells)
    {
        HashCode hash = new();
        hash.Add(key);
        AddCellsToSignature(ref hash, materialCells);
        AddCellsToSignature(ref hash, occluderCells);
        return hash.ToHashCode();
    }

    private static void AddCellsToSignature(ref HashCode hash, IReadOnlyList<StaticVoxelRenderCell> cells)
    {
        hash.Add(cells.Count);
        foreach (StaticVoxelRenderCell cell in cells)
        {
            hash.Add(cell.Center);
            hash.Add(cell.Size);
            hash.Add(cell.MaterialId);
        }
    }

    public static int CalculateExportSignature(GeometryRenderExport export)
    {
        if (export.StaticContentVersion != 0)
        {
            return CalculateVersionedExportSignature(export);
        }

        HashCode hash = new();
        hash.Add(export.StaticDirtyCells.Count);
        foreach (GeometryCellKey dirtyCell in export.StaticDirtyCells
            .OrderBy(cell => cell.Y)
            .ThenBy(cell => cell.Z)
            .ThenBy(cell => cell.X))
        {
            hash.Add(dirtyCell);
        }

        Dictionary<Cube, GeometryMaterialId> compatibilityMaterials = new(export.Cells.Count);
        foreach (GeometryRenderCell renderCell in export.Cells)
        {
            compatibilityMaterials[renderCell.CompatibilityCube] = renderCell.Cell.Definition.MaterialId;
        }

        hash.Add(export.CompatibilityCubes.Count);
        foreach (Cube cube in export.CompatibilityCubes)
        {
            hash.Add(cube.Position);
            hash.Add(cube.Side);
            GeometryMaterialId materialId = compatibilityMaterials.TryGetValue(cube, out GeometryMaterialId mappedMaterialId)
                ? mappedMaterialId
                : cube.MaterialId;
            hash.Add(materialId);
        }

        hash.Add(export.Blocks.Count);
        foreach (GeometryRenderBlockDescriptor block in export.Blocks)
        {
            hash.Add(block.CellKey);
            hash.Add(block.SubcellKey);
            hash.Add(block.Center);
            hash.Add(block.VisualSize);
            hash.Add(block.MaterialId);
        }

        return hash.ToHashCode();
    }

    // The map's static content version already identifies the static cubes and blocks, so only
    // it and the static dirty cells are hashed. Dirty cells are combined order-independently.
    private static int CalculateVersionedExportSignature(GeometryRenderExport export)
    {
        IReadOnlyList<GeometryCellKey> staticDirtyCells = export.StaticDirtyCells;
        int dirtyCellsHash = 0;
        for (int index = 0; index < staticDirtyCells.Count; index++)
        {
            dirtyCellsHash = unchecked(dirtyCellsHash + HashCode.Combine(staticDirtyCells[index]));
        }

        return HashCode.Combine(export.StaticContentVersion, staticDirtyCells.Count, dirtyCellsHash);
    }

    private static float DistanceSquaredToBounds(Vector3 point, StaticWorldChunkBounds bounds)
    {
        float dx = DistanceOutsideAxis(point.X, bounds.Min.X, bounds.Max.X);
        float dy = DistanceOutsideAxis(point.Y, bounds.Min.Y, bounds.Max.Y);
        float dz = DistanceOutsideAxis(point.Z, bounds.Min.Z, bounds.Max.Z);
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static float DistanceOutsideAxis(float value, float min, float max)
    {
        if (value < min)
        {
            return min - value;
        }

        if (value > max)
        {
            return value - max;
        }

        return 0f;
    }
}

internal sealed class StaticWorldChunkUploadBatch
{
    private readonly Queue<StaticWorldChunkBuildOutput> _pendingOutputs;

    public StaticWorldChunkUploadBatch(StaticWorldChunkBuildBatch batch)
    {
        ExportSignature = batch.ExportSignature;
        DirtyChunks = batch.DirtyChunks;
        _pendingOutputs = new Queue<StaticWorldChunkBuildOutput>(batch.Outputs);
    }

    public int ExportSignature { get; }

    public IReadOnlySet<StaticWorldChunkKey> DirtyChunks { get; }

    public HashSet<StaticWorldChunkMeshKey> ReplacedKeys { get; } = [];

    public bool IsComplete => _pendingOutputs.Count == 0;

    public bool TryTakeNext(out StaticWorldChunkBuildOutput? output)
    {
        if (_pendingOutputs.Count == 0)
        {
            output = null;
            return false;
        }

        output = _pendingOutputs.Dequeue();
        ReplacedKeys.Add(output.Key);
        return true;
    }
}

public readonly record struct StaticWorldChunkRenderStats(
    int VisibleChunks,
    int MeshCount,
    int ExposedFaces,
    int Triangles,
    double SignatureMilliseconds = 0d,
    double ExtractMilliseconds = 0d,
    double PlanMilliseconds = 0d,
    double SyncMilliseconds = 0d,
    double DrawMilliseconds = 0d,
    int RebuiltMeshes = 0,
    int ReusedMeshes = 0);

public readonly record struct StaticWorldChunkSyncStats(
    int RebuiltMeshes,
    int ReusedMeshes);
