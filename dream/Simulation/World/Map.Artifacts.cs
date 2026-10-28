using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

public partial class Map
{
    private void RebuildGeometryCellArtifacts(GeometryCellKey key, bool syncPhysics)
    {
        bool wasMergedStaticSubcellRenderCell = _mergedStaticSubcellRenderCells.Contains(key);
        bool wasMergedStaticSubcellCollisionCell = _mergedStaticSubcellCollisionCells.Contains(key);
        if (!_geometryCells.TryGetValue(key, out GeometryCellRecord? geometryCell))
        {
            _renderBlocksByCell.Remove(key);
            _collisionBodiesByCell.Remove(key);
            MarkRenderDirty(key);
            _pendingCollisionDirtyCells.Add(key);
            if (syncPhysics)
            {
                _physicsBinder?.RemoveCellCollisionBodies(key);
                _physicsBinder?.SyncCellSupportBody(key, needed: false);
            }

            RebuildMergedStaticSubcellRenderBlocksIfNeeded(wasMergedStaticSubcellRenderCell, needsMergedStaticSubcellRender: false);
            RebuildMergedStaticSubcellCollisionBodiesIfNeeded(
                wasMergedStaticSubcellCollisionCell,
                needsMergedStaticSubcellCollision: false,
                syncPhysics);
            return;
        }

        if (!IsHeadless)
        {
            _renderBlocksByCell.TryGetValue(key, out IReadOnlyList<GeometryRenderBlockDescriptor>? previousRenderBlocks);
            _renderBlocksByCell[key] = BuildRenderBlocks(geometryCell, previousRenderBlocks);
        }

        _collisionBodiesByCell.TryGetValue(key, out IReadOnlyList<GeometryCollisionBodyDescriptor>? previousCollisionBodies);
        IReadOnlyList<GeometryCollisionBodyDescriptor> collisionBodies = BuildCollisionBodies(geometryCell, previousCollisionBodies);
        _collisionBodiesByCell[key] = collisionBodies;
        MarkRenderDirty(key);
        _pendingCollisionDirtyCells.Add(key);

        bool needsMergedStaticSubcellCollision = ShouldUseMergedStaticSubcellCollision(geometryCell);
        if (syncPhysics && geometryCell.Key.Y > 0 && !needsMergedStaticSubcellCollision)
        {
            _physicsBinder?.SyncCellCollisionBodies(key, collisionBodies);
        }

        if (syncPhysics)
        {
            _physicsBinder?.SyncCellSupportBody(key, NeedsCellSupport(key));
        }

        RebuildMergedStaticSubcellRenderBlocksIfNeeded(
            wasMergedStaticSubcellRenderCell,
            ShouldUseMergedSubcellRender(geometryCell));
        RebuildMergedStaticSubcellCollisionBodiesIfNeeded(
            wasMergedStaticSubcellCollisionCell,
            needsMergedStaticSubcellCollision,
            syncPhysics);
    }

    // previous is the cell's current descriptor list. Descriptor lists are owned by the map and
    // every export copies them, so the fracture and coarse-cube paths, which run on every
    // physics sync of a moving piece or cube, rewrite a previous array of the right length in
    // place instead of allocating a new one.
    private IReadOnlyList<GeometryRenderBlockDescriptor> BuildRenderBlocks(
        GeometryCellRecord record,
        IReadOnlyList<GeometryRenderBlockDescriptor>? previous)
    {
        if (record.FracturePieces is { Count: > 0 } pieces)
        {
            GeometryRenderBlockDescriptor[] blocks = ReuseOrAllocate(previous, CountValidPieces(pieces));
            int next = 0;
            for (int index = 0; index < pieces.Count; index++)
            {
                GeometryFracturePiece piece = pieces[index];
                if (!piece.IsValid())
                {
                    continue;
                }

                blocks[next++] = _generator.CreateRenderBlockDescriptor(
                    record,
                    piece.Center,
                    piece.SideLength);
            }

            return blocks;
        }

        if (ShouldUseCoarseCompatibilityRender(record))
        {
            Vector3 center = new(record.Key.X + 0.5f, record.Key.Y + 0.5f, record.Key.Z + 0.5f);
            GeometryRenderBlockDescriptor[] blocks = ReuseOrAllocate(previous, 1);
            blocks[0] = _generator.CreateRenderBlockDescriptor(record, center, 1f);
            return blocks;
        }

        if (ShouldUseMergedSubcellRender(record))
        {
            return BuildMergedSubcellRenderBlocks(record);
        }

        return BuildSubcellRenderBlocks(record);
    }

    // Kept apart from BuildRenderBlocks: the lambda's closure over record would otherwise be
    // allocated on entry to BuildRenderBlocks, on every rebuild of a coarse cube cell too.
    private IReadOnlyList<GeometryRenderBlockDescriptor> BuildSubcellRenderBlocks(GeometryCellRecord record)
    {
        return EnumerateSolidSubcells(record)
            .Select(entry => _generator.CreateRenderBlockDescriptor(
                record,
                entry.Center,
                entry.SideLength,
                entry.SubcellKey,
                ResolveSubcellVisualFillScale(record)))
            .ToArray();
    }

    // Same in-place reuse of previous as BuildRenderBlocks.
    private IReadOnlyList<GeometryCollisionBodyDescriptor> BuildCollisionBodies(
        GeometryCellRecord record,
        IReadOnlyList<GeometryCollisionBodyDescriptor>? previous)
    {
        if (record.Key.Y == 0)
        {
            return Array.Empty<GeometryCollisionBodyDescriptor>();
        }

        bool isStatic = _staticPhysicsCells.Contains(record.Key);
        if (record.FracturePieces is { Count: > 0 } pieces)
        {
            GeometryCollisionBodyDescriptor[] fractureBodies = ReuseOrAllocate(previous, CountValidPieces(pieces));
            int next = 0;
            for (int index = 0; index < pieces.Count; index++)
            {
                GeometryFracturePiece piece = pieces[index];
                if (!piece.IsValid())
                {
                    continue;
                }

                fractureBodies[next++] = new GeometryCollisionBodyDescriptor(
                    GeometryCollisionBodyKind.Subcell,
                    piece.Center,
                    new Vector3(piece.SideLength * 0.5f),
                    IsStatic: false,
                    CellKey: record.Key,
                    FracturePieceIndex: index);
            }

            return fractureBodies;
        }

        if (ShouldUseCoarseCompatibilityRender(record))
        {
            Cube? cube = Cubes[record.Key.X, record.Key.Y, record.Key.Z];
            if (cube == null)
            {
                return Array.Empty<GeometryCollisionBodyDescriptor>();
            }

            GeometryCollisionBodyDescriptor[] bodies = ReuseOrAllocate(previous, 1);
            bodies[0] = new GeometryCollisionBodyDescriptor(
                GeometryCollisionBodyKind.Cell,
                cube.Position,
                new Vector3(0.5f, 0.5f, 0.5f),
                isStatic,
                CellKey: record.Key,
                CompatibilityCube: cube);
            return bodies;
        }

        return BuildMergedSubcellCollisionBodies(record, isStatic);
    }

    private static T[] ReuseOrAllocate<T>(IReadOnlyList<T>? previous, int length)
    {
        if (length == 0)
        {
            return Array.Empty<T>();
        }

        return previous is T[] reusable && reusable.Length == length
            ? reusable
            : new T[length];
    }

    // Call after the cell's state has changed. Static dirtiness is tracked against what the cell
    // contributed before and after, so removing or converting static content still invalidates it.
    private void MarkRenderDirty(GeometryCellKey key)
    {
        if (IsHeadless)
        {
            return;
        }

        _pendingRenderDirtyCells.Add(key);
        bool wasStatic = _staticRenderCells.Contains(key);
        bool isStatic = _geometryCells.TryGetValue(key, out GeometryCellRecord? record) &&
            IsStaticRenderContent(record);
        if (isStatic)
        {
            _staticRenderCells.Add(key);
            _dynamicRenderCells.Remove(key);
        }
        else
        {
            _staticRenderCells.Remove(key);
            if (record != null)
            {
                _dynamicRenderCells.Add(key);
            }
            else
            {
                _dynamicRenderCells.Remove(key);
            }
        }

        if (wasStatic || isStatic)
        {
            _pendingStaticRenderDirtyCells.Add(key);
            BumpStaticContentVersion();
        }
    }

    private void BumpStaticContentVersion()
    {
        StaticContentVersion = Interlocked.Increment(ref _staticContentVersionSource);
    }

    // Mirrors the static/dynamic split in CaptureGeometryRenderExport.
    private bool IsStaticRenderContent(GeometryCellRecord record)
    {
        return ShouldUseCoarseCompatibilityRender(record)
            ? IsStaticCompatibilityRender(record.Key)
            : record.FracturePieces == null;
    }

    private static bool ShouldUseCoarseCompatibilityRender(GeometryCellRecord record)
    {
        return record.SubdivisionState == null &&
            record.Definition.ShapeKind == GeometryShapeKind.SolidCube;
    }

    private bool IsStaticCompatibilityRender(GeometryCellKey key)
    {
        return _staticPhysicsCells.Contains(key) || key.Y == 0;
    }

    private static float ResolveSubcellVisualFillScale(GeometryCellRecord record)
    {
        return record.SubdivisionState != null &&
            record.FracturePieces == null &&
            record.Definition.SubdivisionResolution == StartupSculptureSubdivisionResolution
                ? StartupSculptureVisualFillScale
                : 1f;
    }

    private static bool ShouldUseMergedSubcellRender(GeometryCellRecord record)
    {
        return record.SubdivisionState != null &&
            record.FracturePieces == null &&
            record.Definition.SubdivisionResolution == StartupSculptureSubdivisionResolution;
    }

    private bool ShouldUseMergedStaticSubcellCollision(GeometryCellRecord record)
    {
        return ShouldUseMergedSubcellRender(record) && _staticPhysicsCells.Contains(record.Key);
    }

    private IEnumerable<(GeometrySubcellKey SubcellKey, Vector3 Center, float SideLength)> EnumerateSolidSubcells(GeometryCellRecord record)
    {
        int resolution = record.Definition.SubdivisionResolution;
        float sideLength = 1f / resolution;
        Vector3 cellOrigin = new(record.Key.X, record.Key.Y, record.Key.Z);

        for (int x = 0; x < resolution; x++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int z = 0; z < resolution; z++)
                {
                    GeometrySubcellKey subcellKey = new(x, y, z);
                    if (!record.IsChildSolid(subcellKey))
                    {
                        continue;
                    }

                    Vector3 center = cellOrigin + new Vector3(
                        (x + 0.5f) * sideLength,
                        (y + 0.5f) * sideLength,
                        (z + 0.5f) * sideLength);
                    yield return (subcellKey, center, sideLength);
                }
            }
        }
    }
}
