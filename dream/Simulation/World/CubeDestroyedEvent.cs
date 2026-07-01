namespace Dream.Simulation.World;

using OpenTK.Mathematics;

public readonly record struct CubeDestroyedEvent(Cube Cube, Vector3 CellPosition);
