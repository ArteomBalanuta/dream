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
    int CollisionBodyCount)
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
        CollisionBodyCount > 0;

    public IEnumerable<string> ToHudLines()
    {
        yield return $"Export: {RenderExportMilliseconds:0.00} ms";
        yield return $"Physics: {PhysicsStepMilliseconds:0.00} ms";
        yield return $"Visible: cubes {VisibleCompatibilityCubes}/{ExportedCompatibilityCubes}, blocks {VisibleGeometryBlocks}/{ExportedGeometryBlocks}";
        yield return $"Batches: cubes {CompatibilityBatchCount}, blocks {GeometryBatchCount}";
        yield return $"Collision Bodies: {CollisionBodyCount}";
    }

    public FramePerformanceSnapshot WithRenderStats(
        int visibleCompatibilityCubes,
        int visibleGeometryBlocks,
        int compatibilityBatchCount,
        int geometryBatchCount)
    {
        return this with
        {
            VisibleCompatibilityCubes = visibleCompatibilityCubes,
            VisibleGeometryBlocks = visibleGeometryBlocks,
            CompatibilityBatchCount = compatibilityBatchCount,
            GeometryBatchCount = geometryBatchCount
        };
    }
}
