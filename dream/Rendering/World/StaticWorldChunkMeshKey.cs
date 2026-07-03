using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.World;

public readonly record struct StaticWorldChunkMeshKey(
    StaticWorldChunkKey ChunkKey,
    GeometryMaterialId MaterialId,
    StaticTerrainMaterialRole MaterialRole);
