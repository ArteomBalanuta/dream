using System.Runtime.CompilerServices;
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

    // GPU bytes one upload of this mesh writes: the vertex buffer plus the 32-bit index buffer.
    public long UploadByteCount =>
        ((long)Vertices.Count * Unsafe.SizeOf<StaticWorldVertex>()) + ((long)Indices.Count * sizeof(uint));

    public StaticWorldChunkMeshInfo ToInfo()
    {
        return new StaticWorldChunkMeshInfo(
            new StaticWorldChunkMeshKey(Key, MaterialId, MaterialRole),
            Bounds,
            TriangleCount,
            ExposedFaceCount);
    }
}

// What the renderer keeps per mesh after upload. The vertex and index lists live only on the GPU.
public readonly record struct StaticWorldChunkMeshInfo(
    StaticWorldChunkMeshKey Key,
    StaticWorldChunkBounds Bounds,
    int TriangleCount,
    int ExposedFaceCount)
{
    public GeometryMaterialId MaterialId => Key.MaterialId;

    public StaticTerrainMaterialRole MaterialRole => Key.MaterialRole;
}

// Uploaded to GL as raw bytes: position at 0, normal at 12, tex coord at 24, 32-byte stride.
// StaticMeshBuffer.VerifyVertexLayout checks this before any upload.
[StructLayout(LayoutKind.Sequential)]
public readonly record struct StaticWorldVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TexCoord);
