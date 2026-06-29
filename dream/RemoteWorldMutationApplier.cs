using OpenTK.Mathematics;

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

        HashSet<(int X, int Y, int Z)> desiredCells = snapshot.PlacedCubeCells
            .Select(ToCellKey)
            .ToHashSet();

        List<Cube> cubesToRemove = map.SolidCubes
            .Where(cube => MathF.Floor(cube.Position.Y + 1e-6f) > 0f)
            .Where(cube => !desiredCells.Contains(ToCellKey(cube.Position)))
            .ToList();
        foreach (Cube cube in cubesToRemove)
        {
            map.RemoveCube(cube);
        }

        foreach ((int x, int y, int z) in desiredCells)
        {
            if (map.TryGetCubeAtCell(x, y, z) == null)
            {
                map.TryAddCube(new Vector3(x, y, z));
            }
        }
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

            default:
                throw new InvalidOperationException($"Unsupported world mutation kind '{mutation.Kind}'.");
        }
    }

    private static (int X, int Y, int Z) ToCellKey(Vector3 cellPosition)
    {
        return (
            (int)MathF.Floor(cellPosition.X + 1e-6f),
            (int)MathF.Floor(cellPosition.Y + 1e-6f),
            (int)MathF.Floor(cellPosition.Z + 1e-6f));
    }
}
