using OpenTK.Mathematics;

namespace Dream.Simulation.World.Geometry;

public static class WorldMicroGrid
{
    public const int MicroCellsPerWorldCell = 16;
    public const float MicroCellSize = 1f / MicroCellsPerWorldCell;

    public static WorldMicroCellKey ToMicroCellKey(Vector3 worldPosition)
    {
        return new WorldMicroCellKey(
            ToMicroCoordinate(worldPosition.X),
            ToMicroCoordinate(worldPosition.Y),
            ToMicroCoordinate(worldPosition.Z));
    }

    public static WorldMicroCellKey ToMicroCellKey(GeometryCellKey parentCell, GeometrySubcellKey localSubcell)
    {
        return new WorldMicroCellKey(
            (parentCell.X * MicroCellsPerWorldCell) + localSubcell.X,
            (parentCell.Y * MicroCellsPerWorldCell) + localSubcell.Y,
            (parentCell.Z * MicroCellsPerWorldCell) + localSubcell.Z);
    }

    public static GeometryCellKey ToParentCellKey(WorldMicroCellKey key)
    {
        return new GeometryCellKey(
            FloorDiv(key.X, MicroCellsPerWorldCell),
            FloorDiv(key.Y, MicroCellsPerWorldCell),
            FloorDiv(key.Z, MicroCellsPerWorldCell));
    }

    public static GeometrySubcellKey ToLocalSubcellKey(WorldMicroCellKey key)
    {
        return new GeometrySubcellKey(
            FloorMod(key.X, MicroCellsPerWorldCell),
            FloorMod(key.Y, MicroCellsPerWorldCell),
            FloorMod(key.Z, MicroCellsPerWorldCell));
    }

    public static Vector3 ToWorldCenter(WorldMicroCellKey key)
    {
        return new Vector3(
            (key.X + 0.5f) * MicroCellSize,
            (key.Y + 0.5f) * MicroCellSize,
            (key.Z + 0.5f) * MicroCellSize);
    }

    private static int ToMicroCoordinate(float worldCoordinate)
    {
        return (int)MathF.Floor(worldCoordinate * MicroCellsPerWorldCell);
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder != 0 && (remainder < 0) != (divisor < 0)
            ? quotient - 1
            : quotient;
    }

    private static int FloorMod(int value, int divisor)
    {
        int result = value % divisor;
        return result < 0 ? result + Math.Abs(divisor) : result;
    }
}
