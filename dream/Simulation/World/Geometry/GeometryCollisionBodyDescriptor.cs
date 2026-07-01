using OpenTK.Mathematics;
using Dream.Simulation.World;

namespace Dream.Simulation.World.Geometry;

public readonly record struct GeometryCollisionBodyDescriptor(
    GeometryCollisionBodyKind Kind,
    Vector3 Center,
    Vector3 HalfExtents,
    bool IsStatic,
    GeometryCellKey? CellKey = null,
    GeometrySubcellKey? SubcellKey = null,
    Cube? CompatibilityCube = null);
