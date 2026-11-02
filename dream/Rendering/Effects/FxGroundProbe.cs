using OpenTK.Mathematics;
using Dream.Simulation.World;

namespace Dream.Rendering.Effects;

// Finds the ground under a player for the contact blob: the top of the first geometry cell at or
// below the feet. Cells are [i, i + 1), so a cell at y has its top face at y + 1.
public static class FxGroundProbe
{
    public const int MaxCellsDown = 8;

    public static bool TryFindGroundY(Map map, Vector3 feet, out float groundY)
    {
        ArgumentNullException.ThrowIfNull(map);
        groundY = 0f;
        if (!float.IsFinite(feet.X) || !float.IsFinite(feet.Y) || !float.IsFinite(feet.Z))
        {
            return false;
        }

        int x = (int)MathF.Floor(feet.X);
        int z = (int)MathF.Floor(feet.Z);
        int top = (int)MathF.Floor(feet.Y - 0.01f);
        for (int y = top; y > top - MaxCellsDown; y--)
        {
            if (map.TryGetGeometryCellAtCell(x, y, z) != null)
            {
                groundY = y + 1;
                return true;
            }
        }

        return false;
    }
}
