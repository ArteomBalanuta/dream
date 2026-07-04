using OpenTK.Mathematics;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Client.Replication;


public static class RemoteWorldMutationApplier
{
    public static void ApplyWorldSnapshot(Map map, WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (snapshot.Width != map.Width || snapshot.Depth != map.Depth)
        {
            throw new InvalidOperationException(
                "Direct map snapshot application requires matching world dimensions.");
        }

        IReadOnlyList<PlacedCubeSnapshot> desiredPlacedCubes = ResolvePlacedCubes(snapshot);
        HashSet<(int X, int Y, int Z)> desiredCells = desiredPlacedCubes
            .Select(cube => ToCellKey(cube.CellPosition))
            .ToHashSet();
        HashSet<(int X, int Y, int Z)> desiredFracturedCells = (snapshot.FracturedCubeCells ?? Array.Empty<Vector3>())
            .Select(ToCellKey)
            .ToHashSet();
        HashSet<(int X, int Y, int Z)> desiredAuthoredSubcellCells = (snapshot.AuthoredSubcellCells ?? Array.Empty<Vector3>())
            .Select(ToCellKey)
            .ToHashSet();
        HashSet<(int X, int Y, int Z)> desiredAllCells = desiredCells
            .Concat(desiredFracturedCells)
            .Concat(desiredAuthoredSubcellCells)
            .ToHashSet();

        List<Cube> cubesToRemove = map.SolidCubes
            .Where(cube => MathF.Floor(cube.Position.Y + 1e-6f) > 0f)
            .Where(cube => !desiredAllCells.Contains(ToCellKey(cube.Position)))
            .ToList();
        foreach (Cube cube in cubesToRemove)
        {
            map.RemoveCube(cube);
        }

        foreach (PlacedCubeSnapshot placedCube in desiredPlacedCubes)
        {
            (int x, int y, int z) = ToCellKey(placedCube.CellPosition);
            GeometryCellKey cell = new(x, y, z);
            Cube? cube = map.TryGetCubeAtCell(x, y, z);
            if (cube == null)
            {
                cube = map.TryAddCube(new Vector3(x, y, z));
            }

            if (cube == null)
            {
                continue;
            }

            map.RestoreSolidCubeCell(cell);
            map.UpdateCubePosition(cube, placedCube.CenterPosition, syncPhysics: true);
        }

        foreach ((int x, int y, int z) in desiredFracturedCells)
        {
            if (map.TryGetCubeAtCell(x, y, z) == null)
            {
                map.TryAddCube(new Vector3(x, y, z));
            }

            map.FractureCubeIntoPieces(new GeometryCellKey(x, y, z));
        }

        map.RestoreGeneratedStartupCells(
            desiredAuthoredSubcellCells.Select(cell => new GeometryCellKey(cell.X, cell.Y, cell.Z)));
    }

    public static void ApplyWorldMutations(Map map, IReadOnlyList<WorldMutationDelta> mutations)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(mutations);

        foreach (WorldMutationDelta mutation in mutations)
        {
            ApplyMutation(map, mutation);
        }
    }

    public static void ApplySnapshotToRuntime(WorldSessionRuntime runtime, ServerSnapshotEnvelope snapshot)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (snapshot.World != null)
        {
            ApplyFullWorldSnapshot(runtime, snapshot.World.Value);
        }

        ApplyWorldMutations(runtime.Map, snapshot.WorldMutations);
    }

    private static void ApplyFullWorldSnapshot(WorldSessionRuntime runtime, WorldSnapshot snapshot)
    {
        if (snapshot.Width != snapshot.Depth)
        {
            throw new InvalidOperationException(
                "Client world snapshot application currently supports only square worlds.");
        }

        if (runtime.Map.Width != snapshot.Width || runtime.Map.Depth != snapshot.Depth)
        {
            runtime.RebuildMapForFloorSize(snapshot.Width);
        }

        ApplyWorldSnapshot(runtime.Map, snapshot);
    }

    private static void ApplyMutation(Map map, WorldMutationDelta mutation)
    {
        (int x, int y, int z) = ToCellKey(mutation.CellPosition);
        switch (mutation.Kind)
        {
            case WorldMutationKind.CubePlaced:
                if (map.TryGetCubeAtCell(x, y, z) == null)
                {
                    map.TryAddCube(new Vector3(x, y, z));
                }
                break;

            case WorldMutationKind.CubeDestroyed:
                Cube? cube = map.TryGetCubeAtCell(x, y, z);
                if (cube != null)
                {
                    map.RemoveCube(cube);
                }
                break;

            case WorldMutationKind.CubeFractured:
                if (map.TryGetCubeAtCell(x, y, z) == null)
                {
                    map.TryAddCube(new Vector3(x, y, z));
                }

                map.FractureCubeIntoPieces(new GeometryCellKey(x, y, z));
                break;

            default:
                throw new InvalidOperationException($"Unsupported world mutation kind '{mutation.Kind}'.");
        }
    }

    private static IReadOnlyList<PlacedCubeSnapshot> ResolvePlacedCubes(WorldSnapshot snapshot)
    {
        if (snapshot.PlacedCubes is { Count: > 0 } placedCubes)
        {
            return placedCubes;
        }

        return snapshot.PlacedCubeCells
            .Select(cell => new PlacedCubeSnapshot(cell, ToCellCenter(cell)))
            .ToArray();
    }

    private static Vector3 ToCellCenter(Vector3 cell)
    {
        (int x, int y, int z) = ToCellKey(cell);
        return new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
    }

    private static (int X, int Y, int Z) ToCellKey(Vector3 cellPosition)
    {
        return (
            (int)MathF.Floor(cellPosition.X + 1e-6f),
            (int)MathF.Floor(cellPosition.Y + 1e-6f),
            (int)MathF.Floor(cellPosition.Z + 1e-6f));
    }
}
