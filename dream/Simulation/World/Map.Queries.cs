using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

public partial class Map
{
    public bool IsInBounds(Vector3 position)
    {
        return _grid.IsInBounds(position);
    }

    public bool TryGetCellForCube(Cube cube, out GeometryCellKey key)
    {
        return _cubeCells.TryGetValue(cube, out key);
    }

    public GeometryCellRecord? TryGetGeometryCellAtCell(int x, int y, int z)
    {
        GeometryCellKey key = new GeometryCellKey(x, y, z);
        return _geometryCells.TryGetValue(key, out GeometryCellRecord? record)
            ? record
            : null;
    }

    public Cube? TryGetCubeAtCell(int x, int y, int z)
    {
        if (!_grid.IsInBounds(new Vector3(x, y, z)))
        {
            return null;
        }

        return Cubes[x, y, z];
    }

    public Cube? TryGetFirstSolidCubeAlongRay(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        bool includeFloor = false)
    {
        if (direction == Vector3.Zero || maxDistance <= 0f)
        {
            return null;
        }

        const float minimumHitDistance = 0.25f;
        Vector3 normalizedDirection = direction.Normalized();
        Cube? closest = null;
        float closestDistance = float.MaxValue;

        // Test the ray against each nearby cube's actual box. Sampling by integer cell missed the
        // visible part of a pushed, off-center cube and hit empty space in its cell instead.
        Vector3 midpoint = origin + (normalizedDirection * (maxDistance * 0.5f));
        foreach (Cube cube in _placedCubeSpatialIndex.EnumerateNearby(midpoint, (maxDistance * 0.5f) + 1f))
        {
            if (!_cubeCells.TryGetValue(cube, out GeometryCellKey key) ||
                (_geometryCells.TryGetValue(key, out GeometryCellRecord? record) && record.SubdivisionState != null))
            {
                continue;
            }

            // Slightly inclusive so a ray running along a face (eye exactly on a cell boundary with
            // float residue in the facing) still hits, as the old cell sampling did.
            Vector3 halfSize = new((cube.Side * 0.5f) + 0.001f);
            if (TryIntersectRayWithBox(origin, normalizedDirection, cube.Position - halfSize, cube.Position + halfSize, out float enter, out float exit) &&
                exit >= minimumHitDistance &&
                enter <= maxDistance &&
                Math.Max(enter, 0f) < closestDistance)
            {
                closest = cube;
                closestDistance = Math.Max(enter, 0f);
            }
        }

        if (includeFloor)
        {
            for (float distance = minimumHitDistance; distance <= Math.Min(maxDistance, closestDistance); distance += 0.10f)
            {
                Vector3 cell = ToCellPosition(origin + (normalizedDirection * distance));
                if (cell.Y == 0 && TryGetCubeAtCell((int)cell.X, 0, (int)cell.Z) is Cube floor)
                {
                    return floor;
                }
            }
        }

        return closest;
    }

    private static bool TryIntersectRayWithBox(
        Vector3 origin,
        Vector3 direction,
        Vector3 min,
        Vector3 max,
        out float enter,
        out float exit)
    {
        enter = float.MinValue;
        exit = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = origin[axis];
            float d = direction[axis];
            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < min[axis] || o > max[axis])
                {
                    return false;
                }

                continue;
            }

            float t1 = (min[axis] - o) / d;
            float t2 = (max[axis] - o) / d;
            enter = Math.Max(enter, Math.Min(t1, t2));
            exit = Math.Min(exit, Math.Max(t1, t2));
            if (enter > exit)
            {
                return false;
            }
        }

        return true;
    }

    public IEnumerable<Cube> GetRenderCandidateCubes(Vector3 position, float radius)
    {
        foreach (Cube cube in GetNearbyFloorCubes(position, radius))
        {
            yield return cube;
        }

        foreach (Cube cube in GetNearbyPlacedCubes(position, radius))
        {
            yield return cube;
        }
    }

    private IEnumerable<Cube> GetNearbyPlacedCubes(Vector3 position, float radius)
    {
        return _placedCubeSpatialIndex.GetNearby(position, radius);
    }

    private static Vector3 ToCellPosition(Vector3 position)
    {
        return new Vector3(
            (int)MathF.Floor(position.X + 1e-6f),
            (int)MathF.Floor(position.Y + 1e-6f),
            (int)MathF.Floor(position.Z + 1e-6f));
    }

    private IEnumerable<Cube> GetNearbyFloorCubes(Vector3 position, float radius)
    {
        int minX = Math.Max(0, (int)MathF.Floor(position.X - radius));
        int maxX = Math.Min(Width - 1, (int)MathF.Ceiling(position.X + radius));
        int minZ = Math.Max(0, (int)MathF.Floor(position.Z - radius));
        int maxZ = Math.Min(Depth - 1, (int)MathF.Ceiling(position.Z + radius));

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                Cube? cube = Cubes[x, 0, z];
                if (cube != null)
                {
                    yield return cube;
                }
            }
        }
    }
}
