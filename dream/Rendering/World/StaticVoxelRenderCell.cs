using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

public readonly record struct StaticVoxelRenderCell(
    Vector3 Center,
    Vector3 Size,
    GeometryMaterialId MaterialId)
{
    public Vector3 Min => Center - Size / 2f;

    public Vector3 Max => Center + Size / 2f;
}
