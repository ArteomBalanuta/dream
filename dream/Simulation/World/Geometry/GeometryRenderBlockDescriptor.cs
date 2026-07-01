using OpenTK.Mathematics;
using Dream.Rendering.Models;

namespace Dream.Simulation.World.Geometry;

public readonly record struct GeometryRenderBlockDescriptor(
    GeometryCellKey CellKey,
    Vector3 Center,
    float SideLength,
    GeometryMaterialId MaterialId,
    AnimatedModel? Animation,
    string ShaderName,
    GeometrySubcellKey? SubcellKey = null);
