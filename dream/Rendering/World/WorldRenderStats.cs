namespace Dream.Rendering.World;

public readonly record struct WorldRenderStats(
    int VisibleCompatibilityCubes,
    int VisibleGeometryBlocks,
    int CompatibilityBatchCount,
    int GeometryBatchCount,
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
    int StaticChunkReusedMeshes = 0)
{
    public static WorldRenderStats Empty => default;
}
