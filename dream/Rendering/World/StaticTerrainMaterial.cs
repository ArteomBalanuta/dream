using Dream.Rendering.Actors;
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

    // Opt-in material response (cubeFragment.glsl reads roughness > 0 as "on"; grain and accentMix are
    // unread there). None, the default, means no response. Uploaded with EquipmentSurface.ToUniform(),
    // which owns the packing, so this cannot drift from the equipment renderers' layout.
    public EquipmentSurface Surface { get; init; } = EquipmentSurface.None;

    public bool HasTexture => !string.IsNullOrWhiteSpace(TexturePath);
}
