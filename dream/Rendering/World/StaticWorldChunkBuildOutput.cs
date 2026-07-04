namespace Dream.Rendering.World;

public sealed record StaticWorldChunkBuildOutput(
    StaticWorldChunkMeshKey Key,
    int Signature,
    StaticWorldChunkMesh Mesh);

public sealed record StaticWorldChunkBuildRequest(
    int ExportSignature,
    IReadOnlySet<StaticWorldChunkKey> DirtyChunks,
    IReadOnlyList<StaticWorldChunkBuildInput> Inputs);

public sealed record StaticWorldChunkBuildBatch(
    int ExportSignature,
    IReadOnlySet<StaticWorldChunkKey> DirtyChunks,
    IReadOnlyList<StaticWorldChunkBuildOutput> Outputs);
