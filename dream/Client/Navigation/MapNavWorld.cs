using Dream.Client.Input;
using Dream.Simulation.Combat;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Client.Navigation;

// Live view of the map: every query reads the geometry records, nothing is cached.
public sealed class MapNavWorld(Map map) : INavWorld, ISightWorld
{
    private readonly Map _map = map;

    public int Width => _map.Width;

    public int Height => _map.Height;

    public int Depth => _map.Depth;

    // Any record blocks, partial cells included. Out of bounds blocks.
    public bool IsBlocked(int x, int y, int z)
        => !InBounds(x, y, z) || _map.TryGetGeometryCellAtCell(x, y, z) != null;

    // Only an intact solid cube can be stood on.
    public bool IsSupport(int x, int y, int z)
    {
        if (!InBounds(x, y, z))
        {
            return false;
        }

        GeometryCellRecord? record = _map.TryGetGeometryCellAtCell(x, y, z);
        return record != null
            && record.Definition.IsSolid
            && record.SubdivisionState == null
            && record.Definition.ShapeKind == GeometryShapeKind.SolidCube;
    }

    // The server's own wall test, so the client stops walking exactly where the server would let a
    // swing through.
    public bool HasClearLine(OpenTK.Mathematics.Vector3 origin, OpenTK.Mathematics.Vector3 anchor)
        => !AuthoritativeCombatResolver.IsBlockedByWall(_map, origin, anchor);

    private bool InBounds(int x, int y, int z)
        => x >= 0 && y >= 0 && z >= 0 && x < Width && y < Height && z < Depth;
}
