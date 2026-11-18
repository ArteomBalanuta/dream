using System.Collections.ObjectModel;

namespace Dream.Rendering.World;

public sealed record StaticWorldChunkBuildOutput(
    StaticWorldChunkMeshKey Key,
    int Signature,
    StaticWorldChunkMesh Mesh);

// Carries everything the worker needs to plan and build: the static cells extracted on the render
// thread (plain values, no Cube references) and a copy of the mesh signatures the renderer will
// hold once its pending upload batch is applied, which the renderer fills in when the build
// starts. The worker never reads Cube objects or the renderer's live signature cache.
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
    // Cells come from the newer request, which already reflects every change since the older one;
    // the renderer takes the signature snapshot when the merged request starts.
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

// PresentChunks is the set of chunk keys the request's cells covered: every chunk the live static
// content still produces a cell for. A resident mesh key outside that set belongs to a chunk that
// simply stopped existing (a map swap to a smaller or differently shaped world, or a merged-block
// reshape that emptied a chunk none of whose own cells were dirtied), and nothing else can ever
// retire it: a chunk with no cells is never planned, so its cached signature is never compared.
// Null means "not known" — a batch that did not come from a cell snapshot retires nothing outside
// its dirty set.
public sealed record StaticWorldChunkBuildBatch(
    int ExportSignature,
    IReadOnlySet<StaticWorldChunkKey> DirtyChunks,
    IReadOnlyList<StaticWorldChunkBuildOutput> Outputs,
    IReadOnlySet<StaticWorldChunkKey>? PresentChunks = null);
