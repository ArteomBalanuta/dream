using System.Collections.ObjectModel;

namespace Dream.Rendering.World;

public sealed record StaticWorldChunkBuildOutput(
    StaticWorldChunkMeshKey Key,
    int Signature,
    StaticWorldChunkMesh Mesh);

// Carries everything the worker needs to plan and build: the static cells extracted on the render
// thread (plain values, no Cube references) and a copy of the mesh signatures applied so far.
// The worker never reads Cube objects or the renderer's live signature cache.
public sealed record StaticWorldChunkBuildRequest(
    int ExportSignature,
    IReadOnlySet<StaticWorldChunkKey> DirtyChunks,
    IReadOnlyList<StaticVoxelRenderCell> Cells,
    IReadOnlyDictionary<StaticWorldChunkMeshKey, int> CachedSignatures)
{
    // A request planned against an empty cache: every mesh of the cells is built.
    public StaticWorldChunkBuildRequest(
        int exportSignature,
        IReadOnlySet<StaticWorldChunkKey> dirtyChunks,
        IReadOnlyList<StaticVoxelRenderCell> cells)
        : this(exportSignature, dirtyChunks, cells, ReadOnlyDictionary<StaticWorldChunkMeshKey, int>.Empty)
    {
    }

    // A newer request replaces an older unbuilt one, but must keep the older one's dirty chunks:
    // a chunk that became empty has no build input, and only its dirty flag removes the stale mesh.
    // Cells and the signature snapshot come from the newer request, which already reflects every
    // change since the cache.
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
