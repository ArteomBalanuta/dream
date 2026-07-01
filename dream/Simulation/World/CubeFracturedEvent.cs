namespace Dream.Simulation.World;

using OpenTK.Mathematics;

public readonly record struct CubeFracturedEvent(Cube Cube, Vector3 CellPosition);
