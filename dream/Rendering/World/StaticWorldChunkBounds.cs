using OpenTK.Mathematics;

namespace Dream.Rendering.World;

public readonly record struct StaticWorldChunkBounds(Vector3 Min, Vector3 Max)
{
    public Vector3 Center => (Min + Max) / 2f;

    public Vector3 Size => Max - Min;

    public static StaticWorldChunkBounds FromCells(IReadOnlyList<StaticVoxelRenderCell> cells)
    {
        if (cells.Count == 0)
        {
            return new StaticWorldChunkBounds(Vector3.Zero, Vector3.Zero);
        }

        Vector3 min = cells[0].Min;
        Vector3 max = cells[0].Max;
        for (int i = 1; i < cells.Count; i++)
        {
            min = Vector3.ComponentMin(min, cells[i].Min);
            max = Vector3.ComponentMax(max, cells[i].Max);
        }

        return new StaticWorldChunkBounds(min, max);
    }
}
