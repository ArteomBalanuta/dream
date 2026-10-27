using OpenTK.Mathematics;

namespace Dream.Simulation.World.Geometry;

public readonly record struct GeometryRenderBlockDescriptor(
    GeometryCellKey CellKey,
    Vector3 Center,
    float SideLength,
    GeometryMaterialId MaterialId,
    string ShaderName,
    GeometrySubcellKey? SubcellKey = null,
    float VisualFillScale = 1f,
    Vector3 SizeOverride = default)
{
    public float VisualSideLength => SideLength * VisualFillScale;

    public Vector3 Size => SizeOverride == default
        ? new Vector3(SideLength)
        : SizeOverride;

    public Vector3 VisualSize => Size * VisualFillScale;
}
