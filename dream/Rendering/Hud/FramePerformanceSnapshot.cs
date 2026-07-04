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
    double ServerBroadcastMilliseconds = 0d)
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
        ServerBroadcastMilliseconds > 0d;

    public IEnumerable<string> ToHudLines()
    {
        yield return $"Frame: total {TotalFrameMilliseconds:0.00} ms, present {SwapBuffersMilliseconds:0.00} ms";
        yield return $"Export: {RenderExportMilliseconds:0.00} ms";
        yield return $"Physics: {PhysicsStepMilliseconds:0.00} ms";
        yield return $"Draw: world {WorldDrawMilliseconds:0.00} ms, viewmodel {ViewModelDrawMilliseconds:0.00} ms, debug {DebugDrawMilliseconds:0.00} ms";
        yield return $"Actors: enemy {EnemyDrawMilliseconds:0.00} ms, local {LocalPlayerDrawMilliseconds:0.00} ms, remote {RemotePlayerDrawMilliseconds:0.00} ms";
        yield return $"HUD: {HudDrawMilliseconds:0.00} ms";
        yield return $"Visible: cubes {VisibleCompatibilityCubes}/{ExportedCompatibilityCubes}, blocks {VisibleGeometryBlocks}/{ExportedGeometryBlocks}";
        yield return $"Batches: cubes {CompatibilityBatchCount}, blocks {GeometryBatchCount}";
        yield return $"Static Chunks: visible {VisibleStaticChunks}, meshes {StaticChunkMeshCount}, faces {StaticChunkExposedFaces}, tris {StaticChunkTriangles}";
        yield return $"Static Timing: signature {StaticChunkSignatureMilliseconds:0.00} ms, extract {StaticChunkExtractMilliseconds:0.00} ms, plan {StaticChunkPlanMilliseconds:0.00} ms, sync {StaticChunkSyncMilliseconds:0.00} ms, draw {StaticChunkDrawMilliseconds:0.00} ms";
        yield return $"Static Cache: rebuilt {StaticChunkRebuiltMeshes}, reused {StaticChunkReusedMeshes}";
        yield return $"Collision Bodies: {CollisionBodyCount}";
        yield return $"Server: tick {ServerTickMilliseconds:0.00} ms, snapshot {ServerSnapshotMilliseconds:0.00} ms, broadcast {ServerBroadcastMilliseconds:0.00} ms";
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
