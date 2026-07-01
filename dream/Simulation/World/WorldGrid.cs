using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Simulation.World;


public class WorldGrid
{
    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public Cube?[,,] Cubes { get; }

    public WorldGrid(int width, int height, int depth)
    {
        Width = width;
        Height = height;
        Depth = depth;
        Cubes = new Cube[width, height, depth];
    }

    public bool IsInBounds(Vector3 position) =>
        position.X >= 0 && position.X < Width &&
        position.Y >= 0 && position.Y < Height &&
        position.Z >= 0 && position.Z < Depth;
}
