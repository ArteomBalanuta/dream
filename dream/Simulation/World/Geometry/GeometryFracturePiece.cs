using OpenTK.Mathematics;

namespace Dream.Simulation.World.Geometry;

public readonly record struct GeometryFracturePiece(
    Vector3 Center,
    float SideLength);
