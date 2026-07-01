namespace Dream.Simulation.World;

using OpenTK.Mathematics;

public readonly record struct CubePlacedEvent(Cube Cube, Vector3 CellPosition);
