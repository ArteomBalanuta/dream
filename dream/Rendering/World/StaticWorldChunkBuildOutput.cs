namespace Dream.Rendering.World;

public sealed record StaticWorldChunkBuildOutput(
    StaticWorldChunkMeshKey Key,
    int Signature,
    StaticWorldChunkMesh Mesh);

public sealed record StaticWorldChunkBuildRequest(
    int ExportSignature,
    IReadOnlySet<StaticWorldChunkKey> DirtyChunks,
    IReadOnlyList<StaticWorldChunkBuildInput> Inputs)
{
    // A newer request replaces an older unbuilt one, but must keep the older one's dirty chunks:
    // a chunk that became empty has no build input, and only its dirty flag removes the stale mesh.
    // Inputs come from the newer plan, which already reflects every change since the cache.
    public static StaticWorldChunkBuildRequest MergeSuperseded(
        StaticWorldChunkBuildRequest older,
        StaticWorldChunkBuildRequest newer)
    {
        return MergeDirtyChunks(newer, older.DirtyChunks);
    }

    public static StaticWorldChunkBuildRequest MergeDirtyChunks(
        StaticWorldChunkBuildRequest request,
        IReadOnlySet<StaticWorldChunkKey> extraDirtyChunks)
    {
        HashSet<StaticWorldChunkKey> dirty = [.. request.DirtyChunks, .. extraDirtyChunks];
        return request with { DirtyChunks = dirty };
    }
}

public sealed record StaticWorldChunkBuildBatch(
    int ExportSignature,
    IReadOnlySet<StaticWorldChunkKey> DirtyChunks,
    IReadOnlyList<StaticWorldChunkBuildOutput> Outputs);
