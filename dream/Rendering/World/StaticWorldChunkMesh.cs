using System.Runtime.InteropServices;
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

// Uploaded to GL as raw bytes: position at 0, normal at 12, tex coord at 24, 32-byte stride.
// StaticMeshBuffer.VerifyVertexLayout checks this before any upload.
[StructLayout(LayoutKind.Sequential)]
public readonly record struct StaticWorldVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TexCoord);
