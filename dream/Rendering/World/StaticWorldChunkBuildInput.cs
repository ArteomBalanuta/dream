using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.World;

public sealed record StaticWorldChunkBuildInput(
    StaticWorldChunkMeshKey Key,
    IReadOnlyList<StaticVoxelRenderCell> MaterialCells,
    IReadOnlyList<StaticVoxelRenderCell> OccluderCells,
    int Signature)
{
    public StaticWorldChunkMesh BuildMesh()
    {
        return StaticWorldChunkMeshBuilder.Build(
            Key.ChunkKey,
            Key.MaterialId,
            Key.MaterialRole,
            MaterialCells,
            OccluderCells);
    }
}
