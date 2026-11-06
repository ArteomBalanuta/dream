using Dream.Rendering.Core;
using Dream.Rendering.Shadows;
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
    // GPU bytes uploaded per frame. One mesh is always uploaded per frame, even when it alone is
    // larger, so a batch always makes progress.
    public const long UploadByteBudgetPerFrame = 2L * 1024 * 1024;
    // Mesh metadata plus the material slot resolved once when the mesh was uploaded.
    private readonly Dictionary<StaticWorldChunkMeshKey, StaticWorldChunkDrawEntry> _meshes = new();
    private readonly Dictionary<StaticWorldChunkMeshKey, int> _signatures = new();
    private readonly Func<StaticWorldChunkBuildRequest, Task<StaticWorldChunkBuildBatch>> _startBuildTask;
    private readonly IRenderFaultSink _faultSink;
    private readonly IStaticWorldChunkMeshStore _meshStore;
    // Two reusable maps. _materialMap is the one the store draws with. _pendingMaterialMap is built
    // from the cells of the request whose batch DrainCompletedBuild has just taken for upload, and
    // is swapped in when that same upload batch finishes applying. Only one upload batch exists at a
    // time, so the active map always describes the most recently applied meshes: it never runs
    // ahead of them (a faulted build has no batch, so it never swaps) and it is not starved by a
    // continuous edit stream, where the applied batch is always older than the newest request. The
    // pending flag is true only between taking a batch and applying it.
    private TerrainMaterialMap _materialMap = new();
    private TerrainMaterialMap _pendingMaterialMap = new();
    private bool _hasPendingMaterialMap;
    // Counts every change of the active map (swap or reset); the store syncs when it moves. Not the
    // maps' own Version, which is per instance.
    private int _materialMapGeneration;
    // The generation the store last saw; -1 until the first sync.
    private int _syncedMaterialMapGeneration = -1;
    // Distinct chunks drawn this frame; cleared each Draw, so the visible-chunk stat counts chunks
    // rather than (chunk, material) meshes without allocating.
    private readonly HashSet<StaticWorldChunkKey> _drawnChunks = new();
    // The visible meshes of this frame and their sort keys (material slot << 32 | visible index).
    // Both arrays are reused across frames and only grow, so a steady-state Draw allocates nothing.
    private StaticWorldChunkMeshInfo[] _visibleMeshes = [];
    // The store's draw handle per visible mesh, so Draw needs no second lookup by key.
    private object?[] _visibleHandles = [];
    private long[] _drawOrder = [];
    private Task<StaticWorldChunkBuildBatch>? _activeBuildTask;
    private StaticWorldChunkBuildRequest? _activeBuildRequest;
    private StaticWorldChunkBuildRequest? _queuedBuildRequest;
    private StaticWorldChunkUploadBatch? _activeUploadBatch;
    // Dirty chunks of failed builds and of exports seen during the build-fault backoff, folded into
    // the next planned request so empty-chunk removals survive.
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
        : this(startBuildTask, faultSink, new GlStaticWorldChunkMeshStore())
    {
    }

    // Tests substitute a mesh store that needs no GL context.
    internal StaticWorldChunkRenderer(
        Func<StaticWorldChunkBuildRequest, Task<StaticWorldChunkBuildBatch>>? startBuildTask,
        IRenderFaultSink? faultSink,
        IStaticWorldChunkMeshStore meshStore)
    {
        ArgumentNullException.ThrowIfNull(meshStore);
        _startBuildTask = startBuildTask ?? StartDefaultBuildTask;
        _faultSink = faultSink ?? RenderFaultLog.Shared;
        _meshStore = meshStore;
    }

    // Runs on the build worker: plans from the request's cell snapshot and signature copy, then
    // builds the meshes. Touches no Cube and no renderer state.
    // The material map of the full cells of the last applied build. The terrain shader blends top
    // faces with it; it is built from the cells of each completed build and becomes active when that
    // build's upload batch has been applied, and is cleared with the buffers.
    internal TerrainMaterialMap TerrainMaterialMap => _materialMap;

    // Increments whenever the active map changes (a batch applied, or a reset).
    internal int TerrainMaterialMapGeneration => _materialMapGeneration;

    // True between taking a completed build for upload and applying its batch.
    internal bool HasPendingTerrainMaterialMap => _hasPendingMaterialMap;

    public static StaticWorldChunkBuildBatch BuildBatch(StaticWorldChunkBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<StaticWorldChunkBuildInput> inputs = PlanBuildInputs(
            request.Cells,
            request.CachedSignatures,
            request.DirtyChunks);
        return new StaticWorldChunkBuildBatch(
            request.ExportSignature,
            request.DirtyChunks,
            BuildOutputs(inputs));
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
        return StaticWorldChunkPlanner.Plan(cells, cachedSignatures, dirtyChunks);
    }

    public static bool IsChunkVisible(Vector3 cameraPosition, Vector3 cameraFront, StaticWorldChunkMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return IsChunkVisible(cameraPosition, cameraFront, mesh.Bounds);
    }

    public static bool IsChunkVisible(Vector3 cameraPosition, Vector3 cameraFront, StaticWorldChunkBounds bounds)
    {
        if (DistanceSquaredToBounds(cameraPosition, bounds) <=
            WorldVisibility.MaxRenderDistance * WorldVisibility.MaxRenderDistance)
        {
            return true;
        }

        return WorldVisibility.IsPositionVisible(
            cameraPosition,
            cameraFront,
            bounds.Center,
            bounds.Size.Length * 0.5f);
    }

    // True when the mesh bounds pass both the distance cull and the view frustum.
    public static bool IsChunkVisible(
        Vector3 cameraPosition,
        Vector3 cameraFront,
        in ViewFrustum frustum,
        StaticWorldChunkBounds bounds)
    {
        return IsChunkVisible(cameraPosition, cameraFront, bounds) &&
            frustum.IntersectsAabb(bounds.Min, bounds.Max);
    }

    // The frustum comes from the camera view and the projection the static mesh draw uses.
    public StaticWorldChunkRenderStats Draw(Render render, Camera camera, GeometryRenderExport export)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(camera);
        ViewFrustum frustum = ViewFrustum.FromViewAndProjection(camera.GetViewMatrix(), render.ProjectionMatrix);
        return Draw(render, camera, frustum, export);
    }

    public StaticWorldChunkRenderStats Draw(Render render, Camera camera, in ViewFrustum frustum, GeometryRenderExport export)
    {
        // Stopwatch timestamps rather than a Stopwatch instance: an unchanged frame allocates nothing.
        // Taken before the drain so the material map rebuild is counted (in the signature bucket).
        long timestamp = Stopwatch.GetTimestamp();
        DrainCompletedBuild();
        int exportSignature = CalculateExportSignature(export);
        double signatureMilliseconds = ElapsedMilliseconds(ref timestamp);

        bool exportUnchanged = _hasAppliedExport && exportSignature == _lastAppliedExportSignature;
        // The render thread only collects the dirty chunks here. Planning (chunk grouping, face
        // occlusion lookups and mesh signatures) runs on the build worker inside BuildBatch.
        HashSet<StaticWorldChunkKey>? dirtyChunks = exportUnchanged ? null : CollectDirtyChunks(export);
        bool buildBackoffActive = ConsumeBuildBackoffFrame();
        // An equal signature means the scheduled request already carries these dirty chunks.
        bool scheduleChange = dirtyChunks != null && exportSignature != _lastScheduledExportSignature;
        double planMilliseconds = ElapsedMilliseconds(ref timestamp);

        double extractMilliseconds = 0d;
        if (scheduleChange && buildBackoffActive)
        {
            // The map drains its static dirty cells on every capture, so a backoff frame keeps
            // them (already unioned with the old retry set) for the request scheduled after it.
            _retryDirtyChunks = dirtyChunks;
        }
        else if (scheduleChange)
        {
            // ExtractCells reads the mutable Cube positions, so it stays on the render thread; the
            // worker gets plain cell values. StartBuild adds the signature snapshot.
            IReadOnlyList<StaticVoxelRenderCell> cells = StaticWorldChunkExtractor.ExtractCells(export);
            extractMilliseconds = ElapsedMilliseconds(ref timestamp);
            ScheduleBuild(new StaticWorldChunkBuildRequest(exportSignature, dirtyChunks!, cells));
        }

        StaticWorldChunkSyncStats syncStats = ApplyPendingUploads();
        double syncMilliseconds = ElapsedMilliseconds(ref timestamp);

        _drawnChunks.Clear();
        int visibleMeshes = 0;
        int visibleFaces = 0;
        int visibleTriangles = 0;
        int distanceCulledMeshes = 0;
        int frustumCulledMeshes = 0;
        Vector3 cameraPosition = camera.Position;
        Vector3 cameraFront = camera.Front;
        EnsureDrawCapacity(_meshes.Count);
        foreach (StaticWorldChunkDrawEntry entry in _meshes.Values)
        {
            StaticWorldChunkMeshInfo mesh = entry.Info;
            // Meshes without faces have no GPU buffer.
            if (mesh.ExposedFaceCount <= 0)
            {
                continue;
            }

            if (!IsChunkVisible(cameraPosition, cameraFront, mesh.Bounds))
            {
                distanceCulledMeshes++;
                continue;
            }

            if (!frustum.IntersectsAabb(mesh.Bounds.Min, mesh.Bounds.Max))
            {
                frustumCulledMeshes++;
                continue;
            }

            if (!_meshStore.TryGetDrawHandle(mesh.Key, out object? handle))
            {
                continue;
            }

            _visibleMeshes[visibleMeshes] = mesh;
            _visibleHandles[visibleMeshes] = handle;
            _drawOrder[visibleMeshes] = ((long)entry.MaterialSlot << 32) | (uint)visibleMeshes;
            _drawnChunks.Add(mesh.Key.ChunkKey);
            visibleMeshes++;
            visibleFaces += mesh.ExposedFaceCount;
            visibleTriangles += mesh.TriangleCount;
        }

        int materialBinds;
        try
        {
            materialBinds = DrawSortedByMaterial(render, camera, visibleMeshes);
        }
        finally
        {
            // The handles are only valid for this pass; dropping them keeps the array from holding
            // buffers that a later sync removes until the slot is overwritten.
            Array.Clear(_visibleHandles, 0, visibleMeshes);
        }

        int visibleChunks = _drawnChunks.Count;

        double drawMilliseconds = ElapsedMilliseconds(ref timestamp);

        return new StaticWorldChunkRenderStats(
            visibleChunks,
            visibleMeshes,
            visibleFaces,
            visibleTriangles,
            signatureMilliseconds,
            extractMilliseconds,
            planMilliseconds,
            syncMilliseconds,
            drawMilliseconds,
            syncStats.RebuiltMeshes,
            syncStats.ReusedMeshes,
            distanceCulledMeshes,
            frustumCulledMeshes,
            materialBinds);
    }

    // Sorts the visible meshes by material slot (material id, then role) and draws them in one
    // pass: each slot is bound once, then all of its meshes are drawn. The sort key carries the
    // visible index in its low bits, so meshes of one slot keep their culling order. Returns the
    // number of material binds, which equals the number of distinct slots drawn.
    private int DrawSortedByMaterial(Render render, Camera camera, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        Span<long> order = _drawOrder.AsSpan(0, count);
        order.Sort();

        int materialBinds = 0;
        int boundSlot = -1;
        if (_materialMapGeneration != _syncedMaterialMapGeneration)
        {
            _meshStore.SyncMaterialMap(_materialMap);
            _syncedMaterialMapGeneration = _materialMapGeneration;
        }

        _meshStore.BeginPass(render, camera);
        for (int i = 0; i < order.Length; i++)
        {
            long key = order[i];
            int slot = (int)(key >> 32);
            if (slot != boundSlot)
            {
                _meshStore.BindMaterial(render, slot);
                boundSlot = slot;
                materialBinds++;
            }

            int visibleIndex = (int)(uint)key;
            _meshStore.Draw(render, in _visibleMeshes[visibleIndex], _visibleHandles[visibleIndex]);
        }

        _meshStore.EndPass(render);
        return materialBinds;
    }

    // The shadow caster walk (Rendering/Shadows): every mesh whose bounds are accepted by the target's light-space
    // frustum is drawn depth-only, with NO material bind, no sort and no camera-direction cull (a mesh behind the camera
    // can still cast into view). Returns the number of meshes drawn. It reuses the visible pass's scratch arrays, which is
    // safe because the two passes never overlap, and clears the handles again for the same reason Draw does.
    public int DrawDepth(Render render, in ShadowCasterView view)
    {
        ArgumentNullException.ThrowIfNull(render);
        EnsureDrawCapacity(_meshes.Count);
        int count = 0;
        foreach (StaticWorldChunkDrawEntry entry in _meshes.Values)
        {
            StaticWorldChunkMeshInfo mesh = entry.Info;
            if (mesh.ExposedFaceCount <= 0 ||
                !view.Accepts(mesh.Bounds.Min, mesh.Bounds.Max) ||
                !_meshStore.TryGetDrawHandle(mesh.Key, out object? handle))
            {
                continue;
            }

            _visibleMeshes[count] = mesh;
            _visibleHandles[count] = handle;
            count++;
        }

        if (count == 0)
        {
            return 0;
        }

        try
        {
            _meshStore.BeginDepthPass(render, view.View, view.Projection);
            for (int i = 0; i < count; i++)
            {
                _meshStore.DrawDepth(render, in _visibleMeshes[i], _visibleHandles[i]);
            }
        }
        finally
        {
            _meshStore.EndPass(render);
            Array.Clear(_visibleHandles, 0, count);
        }

        return count;
    }

    private void EnsureDrawCapacity(int meshCount)
    {
        if (_drawOrder.Length >= meshCount)
        {
            return;
        }

        int capacity = Math.Max(meshCount, Math.Max(16, _drawOrder.Length * 2));
        _visibleMeshes = new StaticWorldChunkMeshInfo[capacity];
        _visibleHandles = new object?[capacity];
        _drawOrder = new long[capacity];
    }

    private static double ElapsedMilliseconds(ref long timestamp)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(timestamp, now).TotalMilliseconds;
        timestamp = now;
        return elapsed;
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
        _meshStore.Clear();
        _materialMap.Reset();
        _pendingMaterialMap.Reset();
        _hasPendingMaterialMap = false;
        _materialMapGeneration++;
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
        _meshStore.Remove(key);
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

        if (completed == null)
        {
            // HandleBuildFault reads _activeBuildRequest to retry its dirty chunks, then clears it.
            HandleBuildFault(new InvalidOperationException("Static chunk build produced no batch."));
            return;
        }

        // The map belongs to the batch being uploaded, so it is built from this request's own cells
        // (an immutable list, fresh per extraction; a merged superseded request carries the newest
        // request's cells). The pending buffer is free: the previous swap left the old map there.
        // A completed batch always has its request; without one (a bug) no map is swapped in.
        Debug.Assert(_activeBuildRequest != null, "A completed build must have an active request.");
        if (_activeBuildRequest != null)
        {
            _pendingMaterialMap.Rebuild(_activeBuildRequest.Cells);
        }

        _hasPendingMaterialMap = _activeBuildRequest != null;

        _activeBuildRequest = null;
        _consecutiveBuildFaults = 0;
        // A batch already superseded by the queued request is still uploaded. Dropping it starved
        // the uploads for as long as an edit arrived every frame and builds took longer than one.
        // It is at most one export old, and the queued request, planned against the signatures
        // this batch leaves behind, corrects whatever changed since.
        _activeUploadBatch = new StaticWorldChunkUploadBatch(completed);
        if (_queuedBuildRequest != null)
        {
            StaticWorldChunkBuildRequest nextRequest = _queuedBuildRequest;
            _queuedBuildRequest = null;
            StartBuild(nextRequest);
        }
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
        long uploadedBytes = 0;
        while (_activeUploadBatch.TryPeekNext(out StaticWorldChunkBuildOutput? next))
        {
            ArgumentNullException.ThrowIfNull(next);
            long meshBytes = next.Mesh.ExposedFaceCount > 0 ? next.Mesh.UploadByteCount : 0;
            if (uploadedBytes > 0 && uploadedBytes + meshBytes > UploadByteBudgetPerFrame)
            {
                break;
            }

            _activeUploadBatch.TryTakeNext(out _);
            ReplaceMesh(next);
            uploadedBytes += meshBytes;
            rebuiltMeshes++;
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
            if (_hasPendingMaterialMap)
            {
                // The meshes of the batch the pending map was built from are in: the blend may now
                // see it.
                (_materialMap, _pendingMaterialMap) = (_pendingMaterialMap, _materialMap);
                _hasPendingMaterialMap = false;
                _materialMapGeneration++;
            }

            reusedMeshes = _meshes.Count - rebuiltMeshes;
            _activeUploadBatch = null;
        }

        return new StaticWorldChunkSyncStats(rebuiltMeshes, Math.Max(0, reusedMeshes));
    }

    private void ReplaceMesh(StaticWorldChunkBuildOutput output)
    {
        // Only metadata is kept; the vertex and index lists go to the GPU and are then dropped.
        // The material slot is resolved here, once per upload, so Draw never touches the catalog.
        _meshes[output.Key] = new StaticWorldChunkDrawEntry(
            output.Mesh.ToInfo(),
            StaticTerrainMaterialCatalog.ResolveSlot(output.Key.MaterialId, output.Key.MaterialRole));
        _signatures[output.Key] = output.Signature;

        if (output.Mesh.ExposedFaceCount > 0)
        {
            _meshStore.Upload(output.Key, output.Mesh);
        }
        else
        {
            _meshStore.Remove(output.Key);
        }
    }

    private void StartBuild(StaticWorldChunkBuildRequest request)
    {
        // The snapshot is taken when the build starts, not when it was scheduled, and includes the
        // pending upload batch: a build's result is applied only after that batch completes, so it
        // may skip only meshes whose signature after that batch matches. A snapshot of a partly
        // applied batch let a reverted edit skip a non-dirty neighbour mesh that the batch then
        // overwrote with the edited version.
        request = request with { CachedSignatures = SnapshotSignaturesAfterPendingUpload() };
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

    // The mesh signatures once the pending upload batch, if any, is fully applied, including the
    // stale-mesh removal it runs on completion.
    private Dictionary<StaticWorldChunkMeshKey, int> SnapshotSignaturesAfterPendingUpload()
    {
        Dictionary<StaticWorldChunkMeshKey, int> snapshot = new(_signatures);
        _activeUploadBatch?.ApplyRemainingSignaturesTo(snapshot);
        return snapshot;
    }

    private static Task<StaticWorldChunkBuildBatch> StartDefaultBuildTask(StaticWorldChunkBuildRequest request)
    {
        return Task.Run(() => BuildBatch(request));
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

    public bool TryPeekNext(out StaticWorldChunkBuildOutput? output)
    {
        return _pendingOutputs.TryPeek(out output);
    }

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

    // Applies the signature changes the rest of this batch makes: its pending outputs, then the
    // removal of every mesh of a dirty chunk that the batch does not rebuild.
    public void ApplyRemainingSignaturesTo(Dictionary<StaticWorldChunkMeshKey, int> signatures)
    {
        HashSet<StaticWorldChunkMeshKey> pendingKeys = [];
        foreach (StaticWorldChunkBuildOutput output in _pendingOutputs)
        {
            signatures[output.Key] = output.Signature;
            pendingKeys.Add(output.Key);
        }

        foreach (StaticWorldChunkMeshKey staleKey in signatures.Keys
            .Where(key => DirtyChunks.Contains(key.ChunkKey) &&
                !ReplacedKeys.Contains(key) &&
                !pendingKeys.Contains(key))
            .ToArray())
        {
            signatures.Remove(staleKey);
        }
    }
}

// VisibleChunks counts distinct chunks with at least one mesh drawn; MeshCount counts the
// (chunk, material) meshes drawn after the distance and frustum culls. The culled counts cover
// meshes with faces that one of the culls skipped this frame.
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
    int ReusedMeshes = 0,
    int DistanceCulledMeshes = 0,
    int FrustumCulledMeshes = 0,
    int MaterialBinds = 0);

// A static mesh's metadata and the terrain material slot resolved when it was uploaded.
internal readonly record struct StaticWorldChunkDrawEntry(
    StaticWorldChunkMeshInfo Info,
    int MaterialSlot);

public readonly record struct StaticWorldChunkSyncStats(
    int RebuiltMeshes,
    int ReusedMeshes);

// Owns the GPU side of the static chunk meshes. The default store keeps one StaticMeshBuffer per
// mesh key and refills it in place on rebuild; tests substitute a store that needs no GL context.
//
// A frame's draws form one batched pass: BeginPass, then for each material slot in ascending
// order one BindMaterial followed by Draw for each of that slot's meshes, then EndPass. A frame
// with no visible mesh makes none of these calls.
internal interface IStaticWorldChunkMeshStore
{
    // Creates the key's buffer, or replaces the contents of the existing one.
    void Upload(StaticWorldChunkMeshKey key, StaticWorldChunkMesh mesh);

    void Remove(StaticWorldChunkMeshKey key);

    // True when the key has a buffer to draw.
    bool Contains(StaticWorldChunkMeshKey key);

    void BeginPass(Render render, Camera camera);

    void BindMaterial(Render render, int materialSlot);

    // Draws a mesh the culling loop found in the store, with the material bound last.
    void Draw(Render render, in StaticWorldChunkMeshInfo mesh);

    // Contains plus the draw handle, in one lookup. The renderer keeps the handle for the pass and
    // hands it back to Draw, so a mesh costs one dictionary lookup per frame. Stores without a
    // handle keep the default: Contains, and Draw by key.
    bool TryGetDrawHandle(StaticWorldChunkMeshKey key, out object? handle)
    {
        handle = null;
        return Contains(key);
    }

    void Draw(Render render, in StaticWorldChunkMeshInfo mesh, object? handle)
    {
        Draw(render, in mesh);
    }

    void EndPass(Render render);

    // The shadow caster pass: BeginDepthPass sets the depth program's matrices once, DrawDepth draws one mesh with no
    // material, EndPass closes it. Stores that draw nothing (the tests') keep the no-op defaults.
    void BeginDepthPass(Render render, in Matrix4 view, in Matrix4 projection)
    {
    }

    void DrawDepth(Render render, in StaticWorldChunkMeshInfo mesh, object? handle)
    {
    }

    // Brings the store's copy of the terrain material map up to date. Called before BeginPass, only
    // when the map changed since the last call. Stores without a GPU copy keep the no-op default.
    void SyncMaterialMap(TerrainMaterialMap map)
    {
    }

    void Clear();
}

internal sealed class GlStaticWorldChunkMeshStore : IStaticWorldChunkMeshStore
{
    private readonly Dictionary<StaticWorldChunkMeshKey, StaticMeshBuffer> _buffers = new();
    private TerrainMaterialMapTexture _mapTexture = new();

    public void SyncMaterialMap(TerrainMaterialMap map)
    {
        _mapTexture.Upload(map);
    }

    public void Upload(StaticWorldChunkMeshKey key, StaticWorldChunkMesh mesh)
    {
        if (_buffers.TryGetValue(key, out StaticMeshBuffer? buffer))
        {
            buffer.Upload(mesh);
            return;
        }

        _buffers[key] = new StaticMeshBuffer(mesh);
    }

    public void Remove(StaticWorldChunkMeshKey key)
    {
        if (_buffers.Remove(key, out StaticMeshBuffer? buffer))
        {
            buffer.Dispose();
        }
    }

    public bool Contains(StaticWorldChunkMeshKey key)
    {
        return _buffers.ContainsKey(key);
    }

    public void BeginPass(Render render, Camera camera)
    {
        render.BeginStaticTerrainPass(camera);
        _mapTexture.Bind(render.GetShader(Render.TerrainShaderName));
    }

    public void BindMaterial(Render render, int materialSlot)
    {
        render.BindStaticTerrainMaterial(materialSlot);
    }

    public void Draw(Render render, in StaticWorldChunkMeshInfo mesh)
    {
        if (_buffers.TryGetValue(mesh.Key, out StaticMeshBuffer? buffer))
        {
            buffer.DrawBatched();
        }
    }

    public bool TryGetDrawHandle(StaticWorldChunkMeshKey key, out object? handle)
    {
        if (_buffers.TryGetValue(key, out StaticMeshBuffer? buffer))
        {
            handle = buffer;
            return true;
        }

        handle = null;
        return false;
    }

    public void Draw(Render render, in StaticWorldChunkMeshInfo mesh, object? handle)
    {
        ((StaticMeshBuffer)handle!).DrawBatched();
    }

    public void EndPass(Render render)
    {
        Render.EndStaticTerrainPass();
    }

    public void BeginDepthPass(Render render, in Matrix4 view, in Matrix4 projection)
    {
        render.BeginShadowDepthProgram(in view, in projection);
    }

    public void DrawDepth(Render render, in StaticWorldChunkMeshInfo mesh, object? handle)
    {
        ((StaticMeshBuffer)handle!).DrawBatched();
    }

    public void Clear()
    {
        foreach (StaticMeshBuffer buffer in _buffers.Values)
        {
            buffer.Dispose();
        }

        _buffers.Clear();
        _mapTexture.Dispose();
        _mapTexture = new TerrainMaterialMapTexture();
    }
}

// Plans the static chunk build inputs on the build worker.
//
// Cells are grouped by chunk in first-appearance order, and by material in first-appearance order
// within a chunk, exactly as the former GroupBy plan did. Occluders of a chunk are its own cells
// plus the cells of the 26 neighbour chunks that can cover one of its faces. Those are found with
// a face lookup keyed by (face direction, quantized face plane, unit grid square) instead of
// copying every cell of all 27 chunks per chunk. Only faces that can touch another chunk are
// inserted or looked up. Cells that cannot touch a face never cover it, so the built faces are
// the same as with the full 27-chunk occluder list.
//
// A mesh signature combines the mesh key, its material cells and a hash of the 27 chunks around it
// (each chunk hashed once), so it changes whenever a cell the old 27-chunk occluder copy contained
// changes, including across chunk borders.
internal static class StaticWorldChunkPlanner
{
    // Must match StaticWorldChunkMeshBuilder's face plane tolerance and quantization.
    private const float Epsilon = 0.0001f;
    // Float slack when deciding which chunks a box reaches.
    private const float BorderSlack = 0.001f;
    // Occluders wider than this on any axis skip the grid and are offered to every neighbour chunk.
    // It also bounds how far from a chunk border a grid occluder can reach, so it is kept small.
    private const float MaxGridOccluderSize = 2f;
    private const int DirectionCount = 6;
    private const int ChunkSide = StaticWorldChunkKey.DefaultChunkSideInWorldUnits;

    public static List<StaticWorldChunkBuildInput> Plan(
        IReadOnlyList<StaticVoxelRenderCell> cells,
        IReadOnlyDictionary<StaticWorldChunkMeshKey, int> cachedSignatures,
        IReadOnlySet<StaticWorldChunkKey> dirtyChunks)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(cachedSignatures);
        ArgumentNullException.ThrowIfNull(dirtyChunks);

        int cellCount = cells.Count;
        StaticVoxelRenderCell[] cellArray = new StaticVoxelRenderCell[cellCount];
        Dictionary<StaticWorldChunkKey, int> chunkIndexByKey = new();
        List<StaticWorldChunkKey> chunkKeys = new();
        List<int> chunkCellCounts = new();
        int[] chunkOfCell = new int[cellCount];
        Vector3 maxGridCellSize = Vector3.Zero;
        float minCellExtent = float.MaxValue;
        for (int i = 0; i < cellCount; i++)
        {
            StaticVoxelRenderCell cell = cells[i];
            cellArray[i] = cell;
            StaticWorldChunkKey key = StaticWorldChunkKey.FromWorldPosition(cell.Center);
            if (!chunkIndexByKey.TryGetValue(key, out int chunkIndex))
            {
                chunkIndex = chunkKeys.Count;
                chunkIndexByKey.Add(key, chunkIndex);
                chunkKeys.Add(key);
                chunkCellCounts.Add(0);
            }

            chunkOfCell[i] = chunkIndex;
            chunkCellCounts[chunkIndex]++;
            if (!IsLargeOccluder(cell))
            {
                maxGridCellSize = Vector3.ComponentMax(maxGridCellSize, cell.Size);
            }

            minCellExtent = MathF.Min(minCellExtent, MathF.Min(cell.Size.X, MathF.Min(cell.Size.Y, cell.Size.Z)));
        }

        int chunkCount = chunkKeys.Count;
        StaticVoxelRenderCell[][] chunkCells = new StaticVoxelRenderCell[chunkCount][];
        int[] fill = new int[chunkCount];
        for (int c = 0; c < chunkCount; c++)
        {
            chunkCells[c] = new StaticVoxelRenderCell[chunkCellCounts[c]];
        }

        for (int i = 0; i < cellCount; i++)
        {
            int c = chunkOfCell[i];
            chunkCells[c][fill[c]++] = cellArray[i];
        }

        int[] chunkHashes = new int[chunkCount];
        for (int c = 0; c < chunkCount; c++)
        {
            HashCode hash = new();
            AddCellsToSignature(ref hash, chunkCells[c]);
            chunkHashes[c] = hash.ToHashCode();
        }

        // A grid occluder covers a face only if it spans the face's other two axes, so a cell whose
        // face it can cover is at most as wide as the grid cells there; only its depth along the
        // face axis is unbounded. Large cells are excluded here when they can never be covered.
        Vector3 maxCoveredDepth = Vector3.Zero;
        foreach (StaticVoxelRenderCell cell in cellArray)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                int uAxis = UAxis(axis);
                int vAxis = VAxis(axis);
                if (Get(cell.Size, uAxis) <= Get(maxGridCellSize, uAxis) + (2f * Epsilon) + BorderSlack &&
                    Get(cell.Size, vAxis) <= Get(maxGridCellSize, vAxis) + (2f * Epsilon) + BorderSlack &&
                    Get(cell.Size, axis) > Get(maxCoveredDepth, axis))
                {
                    Set(ref maxCoveredDepth, axis, Get(cell.Size, axis));
                }
            }
        }

        FaceGeometry geometry = new(
            maxCoveredDepth * 0.5f,
            maxGridCellSize * 0.5f,
            cellCount == 0 ? 0f : MathF.Max(0f, (minCellExtent * 0.5f) - Epsilon) * 0.5f);
        int[] neighborMasks = new int[chunkCount];
        for (int c = 0; c < chunkCount; c++)
        {
            neighborMasks[c] = CalculateNeighborMask(chunkKeys[c], chunkIndexByKey);
        }

        FaceLookup lookup = FaceLookup.Create(cellArray, chunkOfCell, chunkKeys, neighborMasks, geometry, out List<int> largeOccluders);

        List<StaticWorldChunkBuildInput> inputs = new();
        int[] occluderStamp = new int[cellCount];
        List<StaticVoxelRenderCell> neighborOccluders = new();
        List<(GeometryMaterialId MaterialId, List<StaticVoxelRenderCell> Cells)> materialGroups = new();
        for (int c = 0; c < chunkCount; c++)
        {
            StaticWorldChunkKey chunkKey = chunkKeys[c];
            StaticVoxelRenderCell[] ownCells = chunkCells[c];
            int stamp = c + 1;

            neighborOccluders.Clear();
            if (lookup.HasEntries && neighborMasks[c] != 0)
            {
                foreach (StaticVoxelRenderCell cell in ownCells)
                {
                    lookup.CollectForeignOccluders(cell, c, chunkKey, neighborMasks[c], geometry, chunkOfCell, chunkKeys, occluderStamp, stamp, cellArray, neighborOccluders);
                }
            }

            foreach (int large in largeOccluders)
            {
                int largeChunk = chunkOfCell[large];
                if (largeChunk != c &&
                    occluderStamp[large] != stamp &&
                    IsNeighborChunk(chunkKey, chunkKeys[largeChunk]))
                {
                    occluderStamp[large] = stamp;
                    neighborOccluders.Add(cellArray[large]);
                }
            }

            StaticVoxelRenderCell[] occluderCells = neighborOccluders.Count == 0
                ? ownCells
                : [.. ownCells, .. neighborOccluders];
            int neighborhoodHash = CalculateNeighborhoodHash(chunkKey, chunkIndexByKey, chunkHashes);

            GroupByMaterial(ownCells, materialGroups);
            bool chunkDirty = dirtyChunks.Contains(chunkKey);
            foreach ((GeometryMaterialId materialId, List<StaticVoxelRenderCell> group) in materialGroups)
            {
                StaticVoxelRenderCell[] materialCells = group.ToArray();
                foreach (StaticTerrainMaterialRole role in StaticTerrainMaterialCatalog.ResolveRoles(materialId))
                {
                    StaticWorldChunkMeshKey meshKey = new(chunkKey, materialId, role);
                    int signature = CalculateInputSignature(meshKey, materialCells, neighborhoodHash);
                    if (!chunkDirty &&
                        cachedSignatures.TryGetValue(meshKey, out int cachedSignature) &&
                        cachedSignature == signature)
                    {
                        continue;
                    }

                    inputs.Add(new StaticWorldChunkBuildInput(meshKey, materialCells, occluderCells, signature));
                }
            }
        }

        return inputs;
    }

    private static void GroupByMaterial(
        StaticVoxelRenderCell[] chunkCells,
        List<(GeometryMaterialId MaterialId, List<StaticVoxelRenderCell> Cells)> groups)
    {
        groups.Clear();
        foreach (StaticVoxelRenderCell cell in chunkCells)
        {
            List<StaticVoxelRenderCell>? target = null;
            foreach ((GeometryMaterialId materialId, List<StaticVoxelRenderCell> group) in groups)
            {
                if (materialId == cell.MaterialId)
                {
                    target = group;
                    break;
                }
            }

            if (target == null)
            {
                target = new List<StaticVoxelRenderCell>();
                groups.Add((cell.MaterialId, target));
            }

            target.Add(cell);
        }
    }

    private static int CalculateNeighborhoodHash(
        StaticWorldChunkKey chunkKey,
        Dictionary<StaticWorldChunkKey, int> chunkIndexByKey,
        int[] chunkHashes)
    {
        HashCode hash = new();
        for (int x = chunkKey.X - 1; x <= chunkKey.X + 1; x++)
        {
            for (int y = chunkKey.Y - 1; y <= chunkKey.Y + 1; y++)
            {
                for (int z = chunkKey.Z - 1; z <= chunkKey.Z + 1; z++)
                {
                    if (chunkIndexByKey.TryGetValue(new StaticWorldChunkKey(x, y, z), out int index))
                    {
                        hash.Add(true);
                        hash.Add(chunkHashes[index]);
                    }
                    else
                    {
                        hash.Add(false);
                    }
                }
            }
        }

        return hash.ToHashCode();
    }

    private static int CalculateInputSignature(
        StaticWorldChunkMeshKey key,
        IReadOnlyList<StaticVoxelRenderCell> materialCells,
        int neighborhoodHash)
    {
        HashCode hash = new();
        hash.Add(key);
        AddCellsToSignature(ref hash, materialCells);
        hash.Add(neighborhoodHash);
        return hash.ToHashCode();
    }

    private static void AddCellsToSignature(ref HashCode hash, StaticVoxelRenderCell[] cells)
    {
        hash.Add(cells.Length);
        foreach (StaticVoxelRenderCell cell in cells)
        {
            hash.Add(cell.Center);
            hash.Add(cell.Size);
            hash.Add(cell.MaterialId);
        }
    }

    private static void AddCellsToSignature(ref HashCode hash, IReadOnlyList<StaticVoxelRenderCell> cells)
    {
        hash.Add(cells.Count);
        for (int i = 0; i < cells.Count; i++)
        {
            StaticVoxelRenderCell cell = cells[i];
            hash.Add(cell.Center);
            hash.Add(cell.Size);
            hash.Add(cell.MaterialId);
        }
    }

    private static bool IsNeighborChunk(StaticWorldChunkKey a, StaticWorldChunkKey b)
    {
        return Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1 && Math.Abs(a.Z - b.Z) <= 1;
    }

    // Bit per existing neighbour chunk, indexed by NeighborBit; the chunk itself is not included.
    private static int CalculateNeighborMask(
        StaticWorldChunkKey chunkKey,
        Dictionary<StaticWorldChunkKey, int> chunkIndexByKey)
    {
        int mask = 0;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if ((dx | dy | dz) != 0 &&
                        chunkIndexByKey.ContainsKey(new StaticWorldChunkKey(chunkKey.X + dx, chunkKey.Y + dy, chunkKey.Z + dz)))
                    {
                        mask |= 1 << NeighborBit(dx, dy, dz);
                    }
                }
            }
        }

        return mask;
    }

    private static int NeighborBit(int dx, int dy, int dz)
    {
        return ((dx + 1) * 9) + ((dy + 1) * 3) + (dz + 1);
    }

    // False when every point of [min, max] lies in the given chunk or in neighbour chunks that do
    // not exist, so no cell of another neighbour chunk can have its center there.
    private static bool MayReachNeighbor(Vector3 min, Vector3 max, StaticWorldChunkKey chunkKey, int neighborMask)
    {
        if (neighborMask == 0)
        {
            return false;
        }

        AxisReach(min.X, max.X, chunkKey.X, out int xLow, out int xHigh);
        AxisReach(min.Y, max.Y, chunkKey.Y, out int yLow, out int yHigh);
        AxisReach(min.Z, max.Z, chunkKey.Z, out int zLow, out int zHigh);
        for (int dx = xLow; dx <= xHigh; dx++)
        {
            for (int dy = yLow; dy <= yHigh; dy++)
            {
                for (int dz = zLow; dz <= zHigh; dz++)
                {
                    if ((dx | dy | dz) != 0 && (neighborMask & (1 << NeighborBit(dx, dy, dz))) != 0)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void AxisReach(float min, float max, int chunkCoordinate, out int low, out int high)
    {
        float regionMin = (float)chunkCoordinate * ChunkSide;
        low = min - BorderSlack <= regionMin ? -1 : 0;
        high = max + BorderSlack >= regionMin + ChunkSide ? 1 : 0;
    }

    private static int Quantize(float value)
    {
        return (int)MathF.Round(value / Epsilon);
    }

    // Direction d is the face of the covered cell: 0 +X, 1 -X, 2 +Y, 3 -Y, 4 +Z, 5 -Z. The covering
    // occluder's opposite face lies on the same plane (within Epsilon), and its extent on the
    // other two axes spans the covered face (within Epsilon).
    private static int Axis(int direction)
    {
        return direction >> 1;
    }

    private static bool IsPositive(int direction)
    {
        return (direction & 1) == 0;
    }

    private static int UAxis(int axis)
    {
        return axis == 0 ? 1 : 0;
    }

    private static int VAxis(int axis)
    {
        return axis == 2 ? 1 : 2;
    }

    private static float Get(Vector3 value, int axis)
    {
        return axis switch
        {
            0 => value.X,
            1 => value.Y,
            _ => value.Z
        };
    }

    private static void Set(ref Vector3 value, int axis, float component)
    {
        switch (axis)
        {
            case 0:
                value.X = component;
                break;
            case 1:
                value.Y = component;
                break;
            default:
                value.Z = component;
                break;
        }
    }

    // HalfMaxCoveredDepth bounds, per face axis, half the depth of any cell a grid occluder can
    // cover; HalfMaxGridSize bounds half the size of any grid (not large) occluder. GridInset is at
    // most half the smallest cell size minus Epsilon (halved again for float slack): a covered
    // face's center lies at least that far inside the covering occluder's span.
    private readonly record struct FaceGeometry(Vector3 HalfMaxCoveredDepth, Vector3 HalfMaxGridSize, float GridInset);

    private static bool IsLargeOccluder(StaticVoxelRenderCell cell)
    {
        return cell.Size.X > MaxGridOccluderSize ||
            cell.Size.Y > MaxGridOccluderSize ||
            cell.Size.Z > MaxGridOccluderSize;
    }

    // Packs (direction, plane key, grid u, grid v) into one key. Grid coordinates keep their low
    // 14 bits, so far-apart squares can share a slot; that only adds candidates, which are
    // harmless extra occluders, and never drops one.
    private static long FaceSlot(int direction, int planeKey, int u, int v)
    {
        return ((long)direction << 60) |
            ((long)(uint)planeKey << 28) |
            ((long)(u & 0x3FFF) << 14) |
            (long)(v & 0x3FFF);
    }

    private sealed class FaceLookup
    {
        private readonly Dictionary<long, int> _heads;
        private readonly List<int> _entryCells;
        private readonly List<int> _entryNext;

        private FaceLookup(int capacity)
        {
            _heads = new Dictionary<long, int>(capacity);
            _entryCells = new List<int>(capacity);
            _entryNext = new List<int>(capacity);
        }

        public bool HasEntries => _heads.Count > 0;

        public static FaceLookup Create(
            StaticVoxelRenderCell[] cells,
            int[] chunkOfCell,
            List<StaticWorldChunkKey> chunkKeys,
            int[] neighborMasks,
            FaceGeometry geometry,
            out List<int> largeOccluders)
        {
            FaceLookup lookup = new(cells.Length);
            largeOccluders = new List<int>();
            for (int i = 0; i < cells.Length; i++)
            {
                StaticVoxelRenderCell cell = cells[i];
                if (IsLargeOccluder(cell))
                {
                    largeOccluders.Add(i);
                    continue;
                }

                int chunkIndex = chunkOfCell[i];
                if (neighborMasks[chunkIndex] == 0)
                {
                    continue;
                }

                StaticWorldChunkKey chunkKey = chunkKeys[chunkIndex];
                Vector3 min = cell.Min;
                Vector3 max = cell.Max;
                for (int direction = 0; direction < DirectionCount; direction++)
                {
                    int axis = Axis(direction);
                    int uAxis = UAxis(axis);
                    int vAxis = VAxis(axis);
                    // A +X face is covered by an occluder's min X plane, a -X face by its max X plane.
                    float plane = IsPositive(direction) ? Get(min, axis) : Get(max, axis);

                    // Where the center of a cell this occluder covers can lie: across the plane, and
                    // at least GridInset inside the occluder's span on the other axes. If no other
                    // existing chunk reaches into that region, the covered cell is in the
                    // occluder's own chunk and already has it through that chunk's cells.
                    Vector3 inset = new(geometry.GridInset);
                    Vector3 regionMin = min + inset;
                    Vector3 regionMax = max - inset;
                    float halfCovered = Get(geometry.HalfMaxCoveredDepth, axis) + Epsilon;
                    Set(ref regionMin, axis, IsPositive(direction) ? plane - halfCovered : plane - Epsilon);
                    Set(ref regionMax, axis, IsPositive(direction) ? plane + Epsilon : plane + halfCovered);
                    if (!MayReachNeighbor(regionMin, regionMax, chunkKey, neighborMasks[chunkIndex]))
                    {
                        continue;
                    }

                    int uMin = (int)MathF.Floor(Get(min, uAxis) + geometry.GridInset);
                    int uMax = (int)MathF.Floor(Get(max, uAxis) - geometry.GridInset);
                    int vMin = (int)MathF.Floor(Get(min, vAxis) + geometry.GridInset);
                    int vMax = (int)MathF.Floor(Get(max, vAxis) - geometry.GridInset);
                    int planeKey = Quantize(plane);
                    for (int u = uMin; u <= uMax; u++)
                    {
                        for (int v = vMin; v <= vMax; v++)
                        {
                            lookup.Add(FaceSlot(direction, planeKey, u, v), i);
                        }
                    }
                }
            }

            return lookup;
        }

        private void Add(long slot, int cellIndex)
        {
            int entry = _entryCells.Count;
            _entryCells.Add(cellIndex);
            _entryNext.Add(_heads.TryGetValue(slot, out int head) ? head : -1);
            _heads[slot] = entry;
        }

        // Adds every looked-up cell of another neighbour chunk that may cover a face of the cell.
        // A covering occluder spans the face, so its grid range holds the face center's square, and
        // its plane is within one quantization step of the face plane (the builder's own window).
        public void CollectForeignOccluders(
            StaticVoxelRenderCell cell,
            int chunkIndex,
            StaticWorldChunkKey chunkKey,
            int neighborMask,
            FaceGeometry geometry,
            int[] chunkOfCell,
            List<StaticWorldChunkKey> chunkKeys,
            int[] occluderStamp,
            int stamp,
            StaticVoxelRenderCell[] cells,
            List<StaticVoxelRenderCell> occluders)
        {
            Vector3 min = cell.Min;
            Vector3 max = cell.Max;
            for (int direction = 0; direction < DirectionCount; direction++)
            {
                int axis = Axis(direction);
                int uAxis = UAxis(axis);
                int vAxis = VAxis(axis);
                float plane = IsPositive(direction) ? Get(max, axis) : Get(min, axis);

                // Where the center of a grid occluder covering this face can lie (large occluders
                // are offered separately). If that region is inside this chunk, every such
                // occluder is one of the chunk's own cells or would be in a chunk that does not exist.
                Vector3 half = geometry.HalfMaxGridSize + new Vector3(Epsilon);
                Vector3 regionMin = max - half;
                Vector3 regionMax = min + half;
                float halfAxis = Get(half, axis);
                Set(ref regionMin, axis, IsPositive(direction) ? plane - Epsilon : plane - halfAxis);
                Set(ref regionMax, axis, IsPositive(direction) ? plane + halfAxis : plane + Epsilon);
                if (!MayReachNeighbor(regionMin, regionMax, chunkKey, neighborMask))
                {
                    continue;
                }

                int u = (int)MathF.Floor((Get(min, uAxis) + Get(max, uAxis)) * 0.5f);
                int v = (int)MathF.Floor((Get(min, vAxis) + Get(max, vAxis)) * 0.5f);
                int planeKey = Quantize(plane);
                for (int offset = -1; offset <= 1; offset++)
                {
                    if (!_heads.TryGetValue(FaceSlot(direction, unchecked(planeKey + offset), u, v), out int entry))
                    {
                        continue;
                    }

                    for (; entry >= 0; entry = _entryNext[entry])
                    {
                        int candidate = _entryCells[entry];
                        int candidateChunk = chunkOfCell[candidate];
                        if (candidateChunk == chunkIndex ||
                            occluderStamp[candidate] == stamp ||
                            !IsNeighborChunk(chunkKey, chunkKeys[candidateChunk]))
                        {
                            continue;
                        }

                        occluderStamp[candidate] = stamp;
                        occluders.Add(cells[candidate]);
                    }
                }
            }
        }
    }
}
