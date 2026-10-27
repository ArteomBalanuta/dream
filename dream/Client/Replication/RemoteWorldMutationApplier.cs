using OpenTK.Mathematics;
using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Client.Replication;


public static class RemoteWorldMutationApplier
{
    private static readonly TimeSpan FractureFallbackLogInterval = TimeSpan.FromSeconds(5);

    public static void ApplyWorldSnapshot(Map map, WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (snapshot.Width != map.Width || snapshot.Depth != map.Depth)
        {
            throw new InvalidOperationException(
                "Direct map snapshot application requires matching world dimensions.");
        }

        IReadOnlyList<PlacedCubeSnapshot> desiredPlacedCubes = ResolvePlacedCubes(snapshot);
        IReadOnlyList<FracturedCubeSnapshot> desiredFracturedCubes = ResolveFracturedCubes(map, snapshot);
        HashSet<(int X, int Y, int Z)> desiredCells = desiredPlacedCubes
            .Select(cube => ToCellKey(cube.CellPosition))
            .ToHashSet();
        HashSet<(int X, int Y, int Z)> desiredFracturedCells = desiredFracturedCubes
            .Select(cube => ToCellKey(cube.CellPosition))
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
            GeometryCellRecord? existingCell = map.TryGetGeometryCellAtCell(x, y, z);
            if (cube != null &&
                (existingCell?.Definition.MaterialId != placedCube.MaterialId ||
                    existingCell?.SubdivisionState != null))
            {
                map.RemoveCube(cube);
                cube = null;
            }

            if (cube == null)
            {
                cube = map.TryAddCube(
                    new Vector3(x, y, z),
                    materialId: placedCube.MaterialId);
            }

            if (cube == null)
            {
                continue;
            }

            map.RestoreSolidCubeCell(cell);
            map.UpdateCubePosition(cube, placedCube.CenterPosition, syncPhysics: true);
            if (placedCube.ActorId is int actorId)
            {
                map.AssignReplicatedCubeId(cube, actorId);
            }
        }

        foreach (FracturedCubeSnapshot fracturedCube in desiredFracturedCubes)
        {
            (int x, int y, int z) = ToCellKey(fracturedCube.CellPosition);
            if (fracturedCube.Pieces.Count > 0)
            {
                map.RestoreFracturePieces(
                    new GeometryCellKey(x, y, z),
                    fracturedCube.MaterialId,
                    fracturedCube.Pieces,
                    withPhysics: true);
                continue;
            }

            ApplyFallbackFracture(map, new GeometryCellKey(x, y, z), fracturedCube.MaterialId, "snapshot");
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

    public static void ApplySnapshotToRuntime(IClientWorldState runtime, ServerSnapshotEnvelope snapshot)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (snapshot.World != null)
        {
            ApplyFullWorldSnapshot(runtime, snapshot.World.Value);
            if (snapshot.World.Value.MutationSequence is ulong worldSequence)
            {
                runtime.AcknowledgeReplicatedWorldMutations(worldSequence);
            }
        }

        ApplyWorldMutations(runtime.Map, TakeUnappliedMutations(runtime, snapshot));
        if (snapshot.WorldChecksum.HasValue)
        {
            // The server exported every delta up to this sequence in this or earlier snapshots
            // (same-cell moves may have been coalesced away).
            runtime.AcknowledgeReplicatedWorldMutations(snapshot.WorldChecksumSequence);
        }
    }

    // True when the snapshot carries a world checksum and the client's world, after applying
    // everything up to that point, no longer matches it. The client then requests a resync.
    public static bool IsWorldOutOfSync(IClientWorldState runtime, ServerSnapshotEnvelope snapshot)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return snapshot.WorldChecksum is ulong expected &&
            runtime.LastAppliedReplicatedWorldMutationSequence == snapshot.WorldChecksumSequence &&
            WorldStateChecksum.Compute(runtime.Map) != expected;
    }

    private static IReadOnlyList<WorldMutationDelta> TakeUnappliedMutations(
        IClientWorldState runtime,
        ServerSnapshotEnvelope snapshot)
    {
        List<WorldMutationDelta> unapplied = new(snapshot.WorldMutations.Count);
        foreach (WorldMutationDelta mutation in snapshot.WorldMutations)
        {
            // Unsequenced deltas predate sequencing and are always applied.
            if (mutation.Sequence == 0)
            {
                unapplied.Add(mutation);
                continue;
            }

            // A full world snapshot already reflects every delta exported alongside it.
            bool alreadyApplied = snapshot.World != null ||
                mutation.Sequence <= runtime.LastAppliedReplicatedWorldMutationSequence;
            runtime.AcknowledgeReplicatedWorldMutations(mutation.Sequence);
            if (!alreadyApplied)
            {
                unapplied.Add(mutation);
            }
        }

        return unapplied;
    }

    private static void ApplyFullWorldSnapshot(IClientWorldState runtime, WorldSnapshot snapshot)
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
                GeometryMaterialId placementMaterial = mutation.MaterialId ?? GeometryMaterialId.Grass;
                Cube? placedCube = map.TryGetCubeAtCell(x, y, z);
                GeometryCellRecord? placedCell = map.TryGetGeometryCellAtCell(x, y, z);
                if (placedCube != null &&
                    (placedCell?.Definition.MaterialId != placementMaterial ||
                        placedCell?.SubdivisionState != null))
                {
                    map.RemoveCube(placedCube);
                    placedCube = null;
                }

                if (placedCube == null)
                {
                    placedCube = map.TryAddCube(
                        new Vector3(x, y, z),
                        materialId: placementMaterial);
                }

                if (placedCube != null && mutation.ActorId is int placedId)
                {
                    map.AssignReplicatedCubeId(placedCube, placedId);
                }
                break;

            case WorldMutationKind.CubeDestroyed:
                Cube? cube = mutation.ActorId is int destroyedId && map.TryGetCubeByReplicatedId(destroyedId, out Cube? byId)
                    ? byId
                    : map.TryGetCubeAtCell(x, y, z);
                if (cube != null)
                {
                    map.RemoveCube(cube);
                }
                break;

            case WorldMutationKind.CubeFractured:
                GeometryCellKey fracturedCell = new(x, y, z);
                GeometryMaterialId fracturedMaterial = mutation.MaterialId ?? ResolveExistingMaterial(map, fracturedCell);
                if (mutation.FracturePieces is { Count: > 0 } pieces)
                {
                    // The cube in that cell is the one that fractured; rubble no longer replaces it
                    // implicitly because rubble does not occupy cells.
                    if (map.TryGetCubeAtCell(x, y, z) is Cube fracturedCube)
                    {
                        map.RemoveCube(fracturedCube);
                    }

                    map.RestoreFracturePieces(
                        fracturedCell,
                        fracturedMaterial,
                        pieces,
                        withPhysics: true);
                    break;
                }

                ApplyFallbackFracture(map, fracturedCell, fracturedMaterial, $"mutation seq={mutation.Sequence}");
                break;

            case WorldMutationKind.CubeMoved:
                ApplyCubeMovedMutation(map, mutation, x, y, z);
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

    private static IReadOnlyList<FracturedCubeSnapshot> ResolveFracturedCubes(Map map, WorldSnapshot snapshot)
    {
        if (snapshot.FracturedCubes is { Count: > 0 } fracturedCubes)
        {
            return fracturedCubes;
        }

        // Legacy snapshots list fractured cells without a material; keep whatever the client
        // already knows about the cell.
        return (snapshot.FracturedCubeCells ?? Array.Empty<Vector3>())
            .Select(cell =>
            {
                (int x, int y, int z) = ToCellKey(cell);
                return new FracturedCubeSnapshot(
                    cell,
                    ResolveExistingMaterial(map, new GeometryCellKey(x, y, z)),
                    Array.Empty<GeometryFracturePiece>());
            })
            .ToArray();
    }

    private static GeometryMaterialId ResolveExistingMaterial(Map map, GeometryCellKey cell)
    {
        return map.TryGetGeometryCellAtCell(cell.X, cell.Y, cell.Z)?.Definition.MaterialId ??
            map.TryGetRubbleAtCell(cell)?.Definition.MaterialId ??
            GeometryMaterialId.Grass;
    }

    // The server sent a fracture without its pieces. Rubble is seeded by cell, so fracturing locally
    // reproduces the server's pieces; the material is the one the caller resolved.
    private static void ApplyFallbackFracture(Map map, GeometryCellKey cell, GeometryMaterialId materialId, string source)
    {
        NetworkDiagnosticsLog.WriteEvery(
            "client.world.fracture-fallback",
            FractureFallbackLogInterval,
            "client.world.fracture-fallback",
            () => $"source={source} cell=({cell.X},{cell.Y},{cell.Z}) material={materialId}");

        if (map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z) == null)
        {
            map.TryAddCube(new Vector3(cell.X, cell.Y, cell.Z), materialId: materialId);
        }

        if (!map.FractureCubeIntoPieces(cell))
        {
            return;
        }

        if (map.TryGetRubbleAtCell(cell) is { FracturePieces: { } pieces } rubble &&
            rubble.Definition.MaterialId != materialId)
        {
            map.RestoreFracturePieces(cell, materialId, pieces, withPhysics: true);
        }
    }

    private static void ApplyCubeMovedMutation(Map map, WorldMutationDelta mutation, int sourceX, int sourceY, int sourceZ)
    {
        Vector3 authoritativeCenter = mutation.CenterPosition ??
            ToCellCenter(mutation.TargetCellPosition ?? mutation.CellPosition);
        // Resolve by server identity first: a cell lookup can pick up a different cube that the
        // client still has in the source cell.
        Cube? cube = mutation.ActorId is int movedId && map.TryGetCubeByReplicatedId(movedId, out Cube? byId)
            ? byId
            : map.TryGetCubeAtCell(sourceX, sourceY, sourceZ);
        if (cube == null && mutation.TargetCellPosition.HasValue)
        {
            (int targetX, int targetY, int targetZ) = ToCellKey(mutation.TargetCellPosition.Value);
            cube = map.TryGetCubeAtCell(targetX, targetY, targetZ);
        }

        // A move never creates a cube: materializing one here duplicated whichever copy the
        // client already had elsewhere and lost its material.
        if (cube == null)
        {
            NetworkDiagnosticsLog.Write(
                "client.world.move-miss",
                () => $"source=({sourceX},{sourceY},{sourceZ}) target={mutation.TargetCellPosition} seq={mutation.Sequence}");
            return;
        }

        map.UpdateCubePosition(cube, authoritativeCenter, syncPhysics: true);
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
