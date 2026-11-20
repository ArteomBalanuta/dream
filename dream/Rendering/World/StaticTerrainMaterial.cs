using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

public readonly record struct StaticTerrainMaterial(
    GeometryMaterialId MaterialId,
    StaticTerrainMaterialRole Role,
    Vector4 BaseColor,
    string TexturePath)
{
    // Linear RGB added after exposure and before fog (cubeFragment.glsl). Zero means not emissive.
    public Vector3 Emissive { get; init; }

    // Opt-in material response (cubeFragment.glsl, uEquipSurface: x metallic, y roughness; z and w are
    // unread there). Zero means no response, the default for every material.
    public Vector4 Surface { get; init; }

    public bool HasTexture => !string.IsNullOrWhiteSpace(TexturePath);
}
