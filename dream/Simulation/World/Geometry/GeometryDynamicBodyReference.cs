using OpenTK.Mathematics;

namespace Dream.Simulation.World.Geometry;

public readonly record struct GeometryDynamicBodyReference(
    GeometryCellKey CellKey,
    int PieceIndex,
    Vector3 InitialCenter);
