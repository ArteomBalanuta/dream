namespace Dream.Rendering.Hud;

public readonly record struct FramePerformanceSnapshot(
    double RenderExportMilliseconds,
    double PhysicsStepMilliseconds,
    int ExportedCompatibilityCubes,
    int ExportedGeometryBlocks,
    int VisibleCompatibilityCubes,
    int VisibleGeometryBlocks,
    int CompatibilityBatchCount,
    int GeometryBatchCount,
    int CollisionBodyCount,
    int VisibleStaticChunks = 0,
    int StaticChunkMeshCount = 0,
    int StaticChunkExposedFaces = 0,
    int StaticChunkTriangles = 0,
    double StaticChunkSignatureMilliseconds = 0d,
    double StaticChunkExtractMilliseconds = 0d,
    double StaticChunkPlanMilliseconds = 0d,
    double StaticChunkSyncMilliseconds = 0d,
    double StaticChunkDrawMilliseconds = 0d,
    int StaticChunkRebuiltMeshes = 0,
    int StaticChunkReusedMeshes = 0,
    double TotalFrameMilliseconds = 0d,
    double EnemyDrawMilliseconds = 0d,
    double LocalPlayerDrawMilliseconds = 0d,
    double RemotePlayerDrawMilliseconds = 0d,
    double WorldDrawMilliseconds = 0d,
    double ViewModelDrawMilliseconds = 0d,
    double DebugDrawMilliseconds = 0d,
    double HudDrawMilliseconds = 0d,
    double SwapBuffersMilliseconds = 0d,
    double ServerTickMilliseconds = 0d,
    double ServerSnapshotMilliseconds = 0d,
    double ServerBroadcastMilliseconds = 0d,
    double PreDrawMilliseconds = 0d,
    double PreDrawClearMilliseconds = 0d,
    double PreDrawCaptureMilliseconds = 0d,
    double PreDrawRemoteStatesMilliseconds = 0d,
    // GPU time per section from GpuFrameTimer. The queries are read back a few frames late,
    // so these describe a frame 2-3 frames older than the CPU timings beside them.
    double GpuWorldMilliseconds = 0d,
    double GpuActorsMilliseconds = 0d,
    double GpuHudMilliseconds = 0d,
    // GL_PRIMITIVES_GENERATED across that same GPU frame.
    long GpuPrimitives = 0,
    // RenderCounters for the previous complete frame.
    int DrawCalls = 0,
    int ProgramBinds = 0,
    int UniformUploads = 0,
    // Appended last so positional callers keep compiling.
    double GpuEffectsMilliseconds = 0d)
{
    public static FramePerformanceSnapshot Empty => default;

    public bool HasCounters =>
        RenderExportMilliseconds > 0d ||
        PhysicsStepMilliseconds > 0d ||
        ExportedCompatibilityCubes > 0 ||
        ExportedGeometryBlocks > 0 ||
        VisibleCompatibilityCubes > 0 ||
        VisibleGeometryBlocks > 0 ||
        CompatibilityBatchCount > 0 ||
        GeometryBatchCount > 0 ||
        CollisionBodyCount > 0 ||
        VisibleStaticChunks > 0 ||
        StaticChunkMeshCount > 0 ||
        StaticChunkExposedFaces > 0 ||
        StaticChunkTriangles > 0 ||
        StaticChunkSignatureMilliseconds > 0d ||
        StaticChunkExtractMilliseconds > 0d ||
        StaticChunkPlanMilliseconds > 0d ||
        StaticChunkSyncMilliseconds > 0d ||
        StaticChunkDrawMilliseconds > 0d ||
        StaticChunkRebuiltMeshes > 0 ||
        StaticChunkReusedMeshes > 0 ||
        TotalFrameMilliseconds > 0d ||
        EnemyDrawMilliseconds > 0d ||
        LocalPlayerDrawMilliseconds > 0d ||
        RemotePlayerDrawMilliseconds > 0d ||
        WorldDrawMilliseconds > 0d ||
        ViewModelDrawMilliseconds > 0d ||
        DebugDrawMilliseconds > 0d ||
        HudDrawMilliseconds > 0d ||
        SwapBuffersMilliseconds > 0d ||
        ServerTickMilliseconds > 0d ||
        ServerSnapshotMilliseconds > 0d ||
        ServerBroadcastMilliseconds > 0d ||
        PreDrawMilliseconds > 0d ||
        PreDrawClearMilliseconds > 0d ||
        PreDrawCaptureMilliseconds > 0d ||
        PreDrawRemoteStatesMilliseconds > 0d ||
        GpuWorldMilliseconds > 0d ||
        GpuActorsMilliseconds > 0d ||
        GpuHudMilliseconds > 0d ||
        GpuEffectsMilliseconds > 0d ||
        GpuPrimitives > 0 ||
        DrawCalls > 0 ||
        ProgramBinds > 0 ||
        UniformUploads > 0;

    public const int HudLineCount = 15;

    // Text for the console log and tests. The HUD draws through TryFormatHudLine instead.
    public IEnumerable<string> ToHudLines()
    {
        for (int index = 0; index < HudLineCount; index++)
        {
            yield return FormatHudLine(index);
        }
    }

    public string FormatHudLine(int index)
    {
        return index switch
        {
            0 => $"Frame: total {TotalFrameMilliseconds:0.00} ms, present {SwapBuffersMilliseconds:0.00} ms",
            1 => $"Export: {RenderExportMilliseconds:0.00} ms",
            2 => $"Physics: {PhysicsStepMilliseconds:0.00} ms",
            3 => $"Draw: world {WorldDrawMilliseconds:0.00} ms, viewmodel {ViewModelDrawMilliseconds:0.00} ms, debug {DebugDrawMilliseconds:0.00} ms",
            4 => $"Actors: enemy {EnemyDrawMilliseconds:0.00} ms, local {LocalPlayerDrawMilliseconds:0.00} ms, remote {RemotePlayerDrawMilliseconds:0.00} ms",
            5 => $"HUD: {HudDrawMilliseconds:0.00} ms",
            6 => $"Visible: cubes {VisibleCompatibilityCubes}/{ExportedCompatibilityCubes}, blocks {VisibleGeometryBlocks}/{ExportedGeometryBlocks}",
            7 => $"Batches: cubes {CompatibilityBatchCount}, blocks {GeometryBatchCount}",
            8 => $"Static Chunks: visible {VisibleStaticChunks}, meshes {StaticChunkMeshCount}, faces {StaticChunkExposedFaces}, tris {StaticChunkTriangles}",
            9 => $"Static Timing: signature {StaticChunkSignatureMilliseconds:0.00} ms, extract {StaticChunkExtractMilliseconds:0.00} ms, plan {StaticChunkPlanMilliseconds:0.00} ms, sync {StaticChunkSyncMilliseconds:0.00} ms, draw {StaticChunkDrawMilliseconds:0.00} ms",
            10 => $"Static Cache: rebuilt {StaticChunkRebuiltMeshes}, reused {StaticChunkReusedMeshes}",
            11 => $"Collision Bodies: {CollisionBodyCount}",
            12 => $"Server: tick {ServerTickMilliseconds:0.00} ms, snapshot {ServerSnapshotMilliseconds:0.00} ms, broadcast {ServerBroadcastMilliseconds:0.00} ms",
            13 => $"Pre-draw: {PreDrawMilliseconds:0.00} ms, clear {PreDrawClearMilliseconds:0.00} ms, capture {PreDrawCaptureMilliseconds:0.00} ms, remote states {PreDrawRemoteStatesMilliseconds:0.00} ms",
            14 => $"GPU: world {GpuWorldMilliseconds:0.00} ms, actors {GpuActorsMilliseconds:0.00} ms, fx {GpuEffectsMilliseconds:0.00} ms, hud {GpuHudMilliseconds:0.00} ms, prims {GpuPrimitives}, draws {DrawCalls}, programs {ProgramBinds}, uniforms {UniformUploads}",
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    // Same text as FormatHudLine, written as UTF-8 with the current culture and no string.
    // HudTextAllocationTests keeps the two in step.
    // Returns false when the destination is too small.
    public bool TryFormatHudLine(int index, Span<byte> destination, out int bytesWritten)
    {
        return index switch
        {
            0 => HudText.TryWrite(destination, $"Frame: total {TotalFrameMilliseconds:0.00} ms, present {SwapBuffersMilliseconds:0.00} ms", out bytesWritten),
            1 => HudText.TryWrite(destination, $"Export: {RenderExportMilliseconds:0.00} ms", out bytesWritten),
            2 => HudText.TryWrite(destination, $"Physics: {PhysicsStepMilliseconds:0.00} ms", out bytesWritten),
            3 => HudText.TryWrite(destination, $"Draw: world {WorldDrawMilliseconds:0.00} ms, viewmodel {ViewModelDrawMilliseconds:0.00} ms, debug {DebugDrawMilliseconds:0.00} ms", out bytesWritten),
            4 => HudText.TryWrite(destination, $"Actors: enemy {EnemyDrawMilliseconds:0.00} ms, local {LocalPlayerDrawMilliseconds:0.00} ms, remote {RemotePlayerDrawMilliseconds:0.00} ms", out bytesWritten),
            5 => HudText.TryWrite(destination, $"HUD: {HudDrawMilliseconds:0.00} ms", out bytesWritten),
            6 => HudText.TryWrite(destination, $"Visible: cubes {VisibleCompatibilityCubes}/{ExportedCompatibilityCubes}, blocks {VisibleGeometryBlocks}/{ExportedGeometryBlocks}", out bytesWritten),
            7 => HudText.TryWrite(destination, $"Batches: cubes {CompatibilityBatchCount}, blocks {GeometryBatchCount}", out bytesWritten),
            8 => HudText.TryWrite(destination, $"Static Chunks: visible {VisibleStaticChunks}, meshes {StaticChunkMeshCount}, faces {StaticChunkExposedFaces}, tris {StaticChunkTriangles}", out bytesWritten),
            9 => HudText.TryWrite(destination, $"Static Timing: signature {StaticChunkSignatureMilliseconds:0.00} ms, extract {StaticChunkExtractMilliseconds:0.00} ms, plan {StaticChunkPlanMilliseconds:0.00} ms, sync {StaticChunkSyncMilliseconds:0.00} ms, draw {StaticChunkDrawMilliseconds:0.00} ms", out bytesWritten),
            10 => HudText.TryWrite(destination, $"Static Cache: rebuilt {StaticChunkRebuiltMeshes}, reused {StaticChunkReusedMeshes}", out bytesWritten),
            11 => HudText.TryWrite(destination, $"Collision Bodies: {CollisionBodyCount}", out bytesWritten),
            12 => HudText.TryWrite(destination, $"Server: tick {ServerTickMilliseconds:0.00} ms, snapshot {ServerSnapshotMilliseconds:0.00} ms, broadcast {ServerBroadcastMilliseconds:0.00} ms", out bytesWritten),
            13 => HudText.TryWrite(destination, $"Pre-draw: {PreDrawMilliseconds:0.00} ms, clear {PreDrawClearMilliseconds:0.00} ms, capture {PreDrawCaptureMilliseconds:0.00} ms, remote states {PreDrawRemoteStatesMilliseconds:0.00} ms", out bytesWritten),
            14 => HudText.TryWrite(destination, $"GPU: world {GpuWorldMilliseconds:0.00} ms, actors {GpuActorsMilliseconds:0.00} ms, fx {GpuEffectsMilliseconds:0.00} ms, hud {GpuHudMilliseconds:0.00} ms, prims {GpuPrimitives}, draws {DrawCalls}, programs {ProgramBinds}, uniforms {UniformUploads}", out bytesWritten),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    public FramePerformanceSnapshot WithRenderStats(
        int visibleCompatibilityCubes,
        int visibleGeometryBlocks,
        int compatibilityBatchCount,
        int geometryBatchCount,
        int visibleStaticChunks = 0,
        int staticChunkMeshCount = 0,
        int staticChunkExposedFaces = 0,
        int staticChunkTriangles = 0,
        double staticChunkSignatureMilliseconds = 0d,
        double staticChunkExtractMilliseconds = 0d,
        double staticChunkPlanMilliseconds = 0d,
        double staticChunkSyncMilliseconds = 0d,
        double staticChunkDrawMilliseconds = 0d,
        int staticChunkRebuiltMeshes = 0,
        int staticChunkReusedMeshes = 0)
    {
        return this with
        {
            VisibleCompatibilityCubes = visibleCompatibilityCubes,
            VisibleGeometryBlocks = visibleGeometryBlocks,
            CompatibilityBatchCount = compatibilityBatchCount,
            GeometryBatchCount = geometryBatchCount,
            VisibleStaticChunks = visibleStaticChunks,
            StaticChunkMeshCount = staticChunkMeshCount,
            StaticChunkExposedFaces = staticChunkExposedFaces,
            StaticChunkTriangles = staticChunkTriangles,
            StaticChunkSignatureMilliseconds = staticChunkSignatureMilliseconds,
            StaticChunkExtractMilliseconds = staticChunkExtractMilliseconds,
            StaticChunkPlanMilliseconds = staticChunkPlanMilliseconds,
            StaticChunkSyncMilliseconds = staticChunkSyncMilliseconds,
            StaticChunkDrawMilliseconds = staticChunkDrawMilliseconds,
            StaticChunkRebuiltMeshes = staticChunkRebuiltMeshes,
            StaticChunkReusedMeshes = staticChunkReusedMeshes
        };
    }
}
