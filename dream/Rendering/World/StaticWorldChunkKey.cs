using OpenTK.Mathematics;

namespace Dream.Rendering.World;

public readonly record struct StaticWorldChunkKey(int X, int Y, int Z)
{
    public const int DefaultChunkSideInWorldUnits = 16;

    public static StaticWorldChunkKey FromWorldPosition(
        Vector3 position,
        int chunkSide = DefaultChunkSideInWorldUnits)
    {
        return new StaticWorldChunkKey(
            FloorDiv(position.X, chunkSide),
            FloorDiv(position.Y, chunkSide),
            FloorDiv(position.Z, chunkSide));
    }

    private static int FloorDiv(float value, int divisor)
    {
        return (int)MathF.Floor(value / divisor);
    }
}
