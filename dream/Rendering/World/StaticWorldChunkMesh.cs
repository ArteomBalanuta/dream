using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

public sealed record StaticWorldChunkMesh(
    StaticWorldChunkKey Key,
    GeometryMaterialId MaterialId,
    StaticTerrainMaterialRole MaterialRole,
    IReadOnlyList<StaticWorldVertex> Vertices,
    IReadOnlyList<uint> Indices,
    StaticWorldChunkBounds Bounds,
    int SourceCellCount,
    int ExposedFaceCount)
{
    public int TriangleCount => Indices.Count / 3;
}

public readonly record struct StaticWorldVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TexCoord);
