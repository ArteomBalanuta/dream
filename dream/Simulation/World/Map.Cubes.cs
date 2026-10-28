using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

public partial class Map
{
    public bool AddCube(
        Vector3 position,
        bool isStatic = false,
        bool withPhysics = true,
        GeometryMaterialId? materialId = null)
    {
        return TryAddCube(position, isStatic, withPhysics, materialId) != null;
    }

    public Cube? TryAddCube(
        Vector3 position,
        bool isStatic = false,
        bool withPhysics = true,
        GeometryMaterialId? materialId = null)
    {
        int x = (int)MathF.Floor(position.X + 1e-6f);
        int y = (int)MathF.Floor(position.Y + 1e-6f);
        int z = (int)MathF.Floor(position.Z + 1e-6f);

        if (!IsInBounds(new Vector3(x, y, z)) || Cubes[x, y, z] != null)
        {
            return null;
        }

        GeometryCellRecord geometryCell = _generator.CreateGeometryCell(
            new Vector3(x, y, z),
            isFloor: y == 0,
            materialId ?? GeometryMaterialId.Grass);
        Cube cube = MaterializeGeometryCell(geometryCell, isStatic, withPhysics);
        return cube;
    }

    public void CopyPlacedCubesTo(Map target)
    {
        foreach (GeometryCellRecord geometryCell in _geometryCells.Values)
        {
            int x = geometryCell.Key.X;
            int y = geometryCell.Key.Y;
            int z = geometryCell.Key.Z;

            if (y == 0)
            {
                continue;
            }

            Vector3 cell = new Vector3(x, y, z);
            if (!target.IsInBounds(cell))
            {
                continue;
            }

            target.TryAddGeometryCell(geometryCell, isStatic: false, withPhysics: target._physics != null);
        }

        foreach (GeometryCellRecord rubble in _rubbleCells.Values)
        {
            target.RestoreFracturePieces(
                rubble.Key,
                rubble.Definition.MaterialId,
                rubble.FracturePieces!,
                withPhysics: target._physics != null);
        }
    }

    public bool RemoveCube(Cube cube)
    {
        if (!TryGetCellForCube(cube, out GeometryCellKey key) || key.Y == 0)
        {
            return false;
        }

        int x = key.X;
        int y = key.Y;
        int z = key.Z;
        RemoveGeometryCell(key);
        _renderBlocksByCell.Remove(key);
        _collisionBodiesByCell.Remove(key);
        _staticPhysicsCells.Remove(key);
        MarkRenderDirty(key);
        _pendingCollisionDirtyCells.Add(key);
        Cubes[x, y, z] = null;
        _solidCubes.Remove(cube);
        _cubeCells.Remove(cube);
        if (_replicatedIdsByCube.Remove(cube, out int replicatedId))
        {
            _cubesByReplicatedId.Remove(replicatedId);
        }

        _placedCubeSpatialIndex.Remove(cube);
        _physicsBinder?.RemoveCellCollisionBodies(key);
        _physicsBinder?.RemoveCompatibilityCubeBodies(cube);
        _physicsBinder?.SyncCellSupportBody(key, needed: false);
        return true;
    }

    public void UpdateCubePosition(Cube cube, Vector3 position, bool syncPhysics = true)
    {
        UpdateCubePosition(cube, position, reservedTargetCells: null, syncPhysics);
    }

    internal bool UpdateCubePosition(
        Cube cube,
        Vector3 position,
        ISet<GeometryCellKey>? reservedTargetCells,
        bool syncPhysics = true)
    {
        if (!_cubeCells.TryGetValue(cube, out GeometryCellKey oldKey) ||
            !IsFinite(position))
        {
            return false;
        }

        if (cube.Position != position)
        {
            _checkpointMovedCubes?.Add(cube);
        }

        GeometryCellKey newKey = new(
            (int)MathF.Floor(position.X + 1e-6f),
            (int)MathF.Floor(position.Y + 1e-6f),
            (int)MathF.Floor(position.Z + 1e-6f));

        if (oldKey == newKey)
        {
            // A static cube moving inside its cell skips MarkRenderDirty when physics is not synced,
            // but its position is part of the static content.
            if (cube.Position != position && _staticRenderCells.Contains(oldKey))
            {
                BumpStaticContentVersion();
            }

            cube.Position = position;
            _placedCubeSpatialIndex.UpdatePosition(cube, position);
            if (syncPhysics && _physicsBinder != null)
            {
                // Replicated moves arrive every tick; moving the live body avoids disposing and
                // reallocating Bullet shape/motion state/body per cube per tick.
                bool sameCellBodyMoved = _physicsBinder.TryMoveCompatibilityCubeBody(cube, oldKey, newKey, position);
                RebuildGeometryCellArtifacts(oldKey, syncPhysics: !sameCellBodyMoved);
            }

            return true;
        }

        if (!IsInBounds(new Vector3(newKey.X, newKey.Y, newKey.Z)) ||
            !_geometryCells.TryGetValue(oldKey, out GeometryCellRecord? oldCell))
        {
            return false;
        }

        if (!CanTransitionCubeToCell(cube, newKey, reservedTargetCells))
        {
            return false;
        }

        cube.Position = position;
        _placedCubeSpatialIndex.UpdatePosition(cube, position);

        // A static cube leaving its cell becomes dynamic in the new one, which needs a new body;
        // every other synced move keeps and reindexes the live body.
        bool bodyMoved = syncPhysics &&
            _physicsBinder != null &&
            !_staticPhysicsCells.Contains(oldKey) &&
            _physicsBinder.TryMoveCompatibilityCubeBody(cube, oldKey, newKey, position);
        bool rebuildPhysics = syncPhysics && _physicsBinder != null && !bodyMoved;

        RemoveGeometryCell(oldKey);
        _renderBlocksByCell.Remove(oldKey, out IReadOnlyList<GeometryRenderBlockDescriptor>? movedRenderBlocks);
        _collisionBodiesByCell.Remove(oldKey, out IReadOnlyList<GeometryCollisionBodyDescriptor>? movedCollisionBodies);
        _staticPhysicsCells.Remove(oldKey);
        Cubes[oldKey.X, oldKey.Y, oldKey.Z] = null;
        // The vacated cell is no longer occupied, so it no longer supports anything; the moved
        // cube is dynamic in its new cell and gets no support there.
        _physicsBinder?.SyncCellSupportBody(oldKey, needed: false);
        RebuildGeometryCellArtifacts(oldKey, syncPhysics: rebuildPhysics);

        // The record is keyed by cell, so a cell change allocates a new one. The old cell's
        // descriptor lists move with it and are rewritten in place for the new cell.
        GeometryCellRecord movedCell = new(
            newKey,
            oldCell.Definition,
            oldCell.SubdivisionState,
            oldCell.FracturePieces);

        SetGeometryCell(newKey, movedCell);
        _cubeCells[cube] = newKey;
        Cubes[newKey.X, newKey.Y, newKey.Z] = cube;
        if (!syncPhysics)
        {
            _physicsBinder?.ReindexCompatibilityCubeBody(cube, oldKey, newKey);
        }

        if (movedRenderBlocks != null)
        {
            _renderBlocksByCell.TryAdd(newKey, movedRenderBlocks);
        }

        if (movedCollisionBodies != null)
        {
            _collisionBodiesByCell.TryAdd(newKey, movedCollisionBodies);
        }

        RebuildGeometryCellArtifacts(newKey, syncPhysics: rebuildPhysics);
        reservedTargetCells?.Add(newKey);
        return true;
    }

    public GeometryMutationResult ApplyGeometrySubcellSolid(
        GeometryCellKey cell,
        GeometrySubcellKey child,
        bool isSolid)
    {
        if (!_geometryCells.TryGetValue(cell, out GeometryCellRecord? record))
        {
            return new GeometryMutationResult(
                ChangedCells: Array.Empty<GeometryCellKey>(),
                SubdivisionActivated: false,
                RenderDirtyCells: Array.Empty<GeometryCellKey>(),
                CollisionDirtyCells: Array.Empty<GeometryCellKey>(),
                RemovedCells: Array.Empty<GeometryCellKey>());
        }

        // The first carve activates the record's subdivision in place, which takes an intact solid
        // cube out of the world checksum; re-hash the record around the change.
        UntrackChecksum(record);
        GeometryMutationResult result = _geometryMutations.SetChildSolid(record, child, isSolid);
        TrackChecksum(record);
        foreach (GeometryCellKey dirtyCell in result.ChangedCells)
        {
            RebuildGeometryCellArtifacts(dirtyCell, syncPhysics: _physicsBinder != null);
        }

        return result;
    }

    public bool RestoreSolidCubeCell(GeometryCellKey cell)
    {
        if (!_geometryCells.TryGetValue(cell, out GeometryCellRecord? existingCell) || cell.Y == 0)
        {
            return false;
        }

        if (existingCell.SubdivisionState != null && existingCell.FracturePieces == null)
        {
            RebuildGeometryCellArtifacts(cell, syncPhysics: _physicsBinder != null);
            return true;
        }

        SetGeometryCell(cell, _generator.CreateGeometryCell(
            new Vector3(cell.X, cell.Y, cell.Z),
            isFloor: false,
            existingCell.Definition.MaterialId));
        RebuildGeometryCellArtifacts(cell, syncPhysics: _physicsBinder != null);
        return true;
    }

    public bool RestoreGeneratedStartupCell(GeometryCellKey cell)
    {
        return RestoreGeneratedStartupCells([cell]) == 1;
    }

    public int RestoreGeneratedStartupCells(IEnumerable<GeometryCellKey> cells)
    {
        HashSet<GeometryCellKey> requestedCells = cells
            .Where(cell => cell.Y > 0 && IsInBounds(new Vector3(cell.X, cell.Y, cell.Z)))
            .ToHashSet();
        if (requestedCells.Count == 0)
        {
            return 0;
        }

        int restoredCount = 0;
        bool wasInitializing = _initializing;
        _initializing = true;
        try
        {
            foreach (GeometryCellRecord generatedCell in _generator.CreateStartupGeometry(Width, Height, Depth, _generateStartupTerrain))
            {
                if (!requestedCells.Contains(generatedCell.Key))
                {
                    continue;
                }

                if (_geometryCells.ContainsKey(generatedCell.Key))
                {
                    SetGeometryCell(generatedCell.Key, generatedCell);
                    RebuildGeometryCellArtifacts(generatedCell.Key, syncPhysics: _physicsBinder != null);
                    restoredCount++;
                    continue;
                }

                if (TryAddGeometryCell(generatedCell, isStatic: true, withPhysics: _physicsBinder != null) != null)
                {
                    restoredCount++;
                }
            }
        }
        finally
        {
            _initializing = wasInitializing;
        }

        if (restoredCount > 0)
        {
            RebuildMergedStaticSubcellRenderBlocks();
            RebuildMergedStaticSubcellCollisionBodies(syncPhysics: _physicsBinder != null);
        }

        return restoredCount;
    }

    private Cube MaterializeGeometryCell(GeometryCellRecord geometryCell, bool isStatic, bool withPhysics)
    {
        Cube cube = _generator.CreateCompatibilityCube(geometryCell);
        int x = geometryCell.Key.X;
        int y = geometryCell.Key.Y;
        int z = geometryCell.Key.Z;
        Cubes[x, y, z] = cube;
        SetGeometryCell(geometryCell.Key, geometryCell);
        _cubeCells[cube] = geometryCell.Key;
        _solidCubes.Add(cube);
        if (y > 0)
        {
            _placedCubeSpatialIndex.Add(cube);
        }

        if (isStatic)
        {
            _staticPhysicsCells.Add(geometryCell.Key);
        }
        else
        {
            _staticPhysicsCells.Remove(geometryCell.Key);
        }

        RebuildGeometryCellArtifacts(geometryCell.Key, syncPhysics: withPhysics && _physicsBinder != null);

        return cube;
    }

    private Cube? TryAddGeometryCell(GeometryCellRecord geometryCell, bool isStatic, bool withPhysics)
    {
        int x = geometryCell.Key.X;
        int y = geometryCell.Key.Y;
        int z = geometryCell.Key.Z;
        if (!IsInBounds(new Vector3(x, y, z)) || Cubes[x, y, z] != null)
        {
            return null;
        }

        return MaterializeGeometryCell(geometryCell, isStatic, withPhysics);
    }

    private bool CanTransitionCubeToCell(
        Cube cube,
        GeometryCellKey newKey,
        ISet<GeometryCellKey>? reservedTargetCells)
    {
        if (reservedTargetCells?.Contains(newKey) == true)
        {
            return false;
        }

        Cube? occupiedCube = Cubes[newKey.X, newKey.Y, newKey.Z];
        if (occupiedCube != null && !ReferenceEquals(occupiedCube, cube))
        {
            return false;
        }

        if (_geometryCells.TryGetValue(newKey, out GeometryCellRecord? record) &&
            !ReferenceEquals(occupiedCube, cube) &&
            record.SubdivisionState != null)
        {
            return false;
        }

        return true;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.X) &&
            !float.IsNaN(value.Y) &&
            !float.IsNaN(value.Z) &&
            !float.IsInfinity(value.X) &&
            !float.IsInfinity(value.Y) &&
            !float.IsInfinity(value.Z);
    }
}
