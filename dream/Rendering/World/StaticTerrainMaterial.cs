using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

public readonly record struct StaticTerrainMaterial(
    GeometryMaterialId MaterialId,
    StaticTerrainMaterialRole Role,
    Vector4 BaseColor,
    string TexturePath)
{
    public bool HasTexture => !string.IsNullOrWhiteSpace(TexturePath);
}
