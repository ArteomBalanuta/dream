namespace Dream.Rendering.World;

public readonly record struct WorldRenderStats(
    int VisibleCompatibilityCubes,
    int VisibleGeometryBlocks,
    int CompatibilityBatchCount,
    int GeometryBatchCount)
{
    public static WorldRenderStats Empty => default;
}
