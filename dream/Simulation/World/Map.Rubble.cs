using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

public partial class Map
{
    public GeometryCellRecord? TryGetRubbleAtCell(GeometryCellKey cell)
    {
        return _rubbleCells.TryGetValue(cell, out GeometryCellRecord? record) ? record : null;
    }

    public bool UpdateFracturePiecePosition(GeometryCellKey cell, int pieceIndex, Vector3 center)
    {
        return UpdateFracturePiecePositions(cell, [(pieceIndex, center)]);
    }

    public bool UpdateFracturePiecePositions(
        GeometryCellKey cell,
        IReadOnlyList<(int PieceIndex, Vector3 Center)> updates)
    {
        return UpdateFracturePiecePositionsCore(cell, updates, acceptedPieceIndices: null);
    }

    public bool UpdateFracturePiecePositions(
        GeometryCellKey cell,
        IReadOnlyList<(int PieceIndex, Vector3 Center)> updates,
        out HashSet<int> acceptedPieceIndices)
    {
        acceptedPieceIndices = [];
        return UpdateFracturePiecePositionsCore(cell, updates, acceptedPieceIndices);
    }

    // Per-tick form: clears acceptedPieceIndices and fills it with the accepted piece indices, so
    // the caller can reuse one set across calls.
    public bool UpdateFracturePiecePositions(
        GeometryCellKey cell,
        IReadOnlyList<(int PieceIndex, Vector3 Center)> updates,
        HashSet<int> acceptedPieceIndices)
    {
        ArgumentNullException.ThrowIfNull(acceptedPieceIndices);
        acceptedPieceIndices.Clear();
        return UpdateFracturePiecePositionsCore(cell, updates, acceptedPieceIndices);
    }

    // Resting pieces (every center unchanged) cost nothing. A moved piece copies the piece list
    // once per call, because the record's list is published through RubbleCells and is never
    // mutated in place; the render and collision descriptors are then rewritten in place.
    private bool UpdateFracturePiecePositionsCore(
        GeometryCellKey cell,
        IReadOnlyList<(int PieceIndex, Vector3 Center)> updates,
        HashSet<int>? acceptedPieceIndices)
    {
        if (!_rubbleCells.TryGetValue(cell, out GeometryCellRecord? record) ||
            record.FracturePieces is not IReadOnlyList<GeometryFracturePiece> currentPieces ||
            updates.Count == 0)
        {
            return false;
        }

        GeometryFracturePiece[]? movedPieces = null;
        bool anyAccepted = false;
        // Indexed loop: enumerating through the interface would box the list's enumerator.
        for (int i = 0; i < updates.Count; i++)
        {
            (int pieceIndex, Vector3 center) = updates[i];
            if (pieceIndex < 0 || pieceIndex >= currentPieces.Count)
            {
                continue;
            }

            // A later update of the same piece compares against the earlier one, as before.
            GeometryFracturePiece piece = movedPieces != null ? movedPieces[pieceIndex] : currentPieces[pieceIndex];
            GeometryFracturePiece movedPiece = piece with { Center = center };
            if (!movedPiece.IsValid())
            {
                continue;
            }

            anyAccepted = true;
            acceptedPieceIndices?.Add(pieceIndex);
            if (piece.Center == center)
            {
                continue;
            }

            movedPieces ??= CopyPieces(currentPieces);
            movedPieces[pieceIndex] = movedPiece;
        }

        if (!anyAccepted)
        {
            return false;
        }

        if (movedPieces == null)
        {
            return true;
        }

        record.SetFracturePieces(movedPieces);
        RebuildRubbleArtifacts(cell, syncPhysics: false);
        return true;
    }

    private static GeometryFracturePiece[] CopyPieces(IReadOnlyList<GeometryFracturePiece> pieces)
    {
        GeometryFracturePiece[] copy = new GeometryFracturePiece[pieces.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = pieces[i];
        }

        return copy;
    }

    public bool FractureCubeIntoPieces(GeometryCellKey cell)
    {
        if (!_geometryCells.TryGetValue(cell, out GeometryCellRecord? record) || cell.Y == 0)
        {
            return false;
        }

        GeometryMaterialId materialId = record.Definition.MaterialId;
        Cube? compatibilityCube = Cubes[cell.X, cell.Y, cell.Z];
        if (compatibilityCube == null || !RemoveCube(compatibilityCube))
        {
            _geometryCells.Remove(cell);
            RebuildGeometryCellArtifacts(cell, syncPhysics: _physicsBinder != null);
        }

        SetRubble(cell, materialId, CubeFracturePattern.CreateFirstPassRubble(cell), _physicsBinder != null);
        return true;
    }

    public bool RestoreFracturePieces(
        GeometryCellKey cell,
        GeometryMaterialId materialId,
        IReadOnlyList<GeometryFracturePiece> pieces,
        bool withPhysics = true)
    {
        if (cell.Y == 0 ||
            pieces.Count == 0 ||
            !IsInBounds(new Vector3(cell.X, cell.Y, cell.Z)))
        {
            return false;
        }

        GeometryFracturePiece[] validPieces = pieces
            .Where(GeometryFracturePiece.IsValid)
            .ToArray();
        if (validPieces.Length == 0)
        {
            return false;
        }

        // Restoring rubble no longer removes a cube in the origin cell: rubble does not occupy it.
        // (Persistence restore used to delete a legitimately placed cube here.)
        SetRubble(cell, materialId, validPieces, withPhysics && _physicsBinder != null);
        return true;
    }

    public bool RemoveRubble(GeometryCellKey cell)
    {
        if (!_rubbleCells.Remove(cell))
        {
            return false;
        }

        RebuildRubbleArtifacts(cell, syncPhysics: _physicsBinder != null);
        return true;
    }

    private void SetRubble(
        GeometryCellKey cell,
        GeometryMaterialId materialId,
        IReadOnlyList<GeometryFracturePiece> pieces,
        bool syncPhysics)
    {
        GeometryCellRecord rubble = _generator.CreateGeometryCell(
            new Vector3(cell.X, cell.Y, cell.Z),
            isFloor: false,
            materialId);
        GeometrySubdivisionState subdivision = rubble.EnsureSubdivisionState();
        int resolution = rubble.Definition.SubdivisionResolution;
        for (int x = 0; x < resolution; x++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int z = 0; z < resolution; z++)
                {
                    subdivision.SetChildSolid(new GeometrySubcellKey(x, y, z), isSolid: false);
                }
            }
        }

        rubble.SetFracturePieces(pieces);
        _rubbleCells[cell] = rubble;
        RebuildRubbleArtifacts(cell, syncPhysics);
    }

    private void RebuildRubbleArtifacts(GeometryCellKey cell, bool syncPhysics)
    {
        _checkpointDirtyRubbleCells?.Add(cell);
        _pendingRenderDirtyCells.Add(cell);
        _pendingCollisionDirtyCells.Add(cell);
        if (!_rubbleCells.TryGetValue(cell, out GeometryCellRecord? rubble))
        {
            _rubbleRenderBlocks.Remove(cell);
            _rubbleCollisionBodies.Remove(cell);
            if (syncPhysics)
            {
                _physicsBinder?.RemoveRubbleBodies(cell);
            }

            return;
        }

        _rubbleRenderBlocks.TryGetValue(cell, out IReadOnlyList<GeometryRenderBlockDescriptor>? previousBlocks);
        _rubbleCollisionBodies.TryGetValue(cell, out IReadOnlyList<GeometryCollisionBodyDescriptor>? previousBodies);
        _rubbleRenderBlocks[cell] = BuildRenderBlocks(rubble, previousBlocks);
        IReadOnlyList<GeometryCollisionBodyDescriptor> bodies = BuildCollisionBodies(rubble, previousBodies);
        _rubbleCollisionBodies[cell] = bodies;
        if (syncPhysics)
        {
            _physicsBinder?.SyncRubbleBodies(cell, bodies);
        }
    }

    public IReadOnlyList<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)>
        CaptureFracturePieces()
    {
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> pieces = [];
        CaptureFracturePiecesInto(pieces);
        return pieces;
    }

    // Appends every valid fracture piece, in CaptureFracturePieces order, without allocating
    // beyond the caller's list growth.
    public void CaptureFracturePiecesInto(
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        foreach (GeometryCellRecord record in _rubbleCells.Values)
        {
            AppendFracturePieces(record, pieces);
        }
    }

    // Appends the valid fracture pieces of one rubble cell; false when the cell has no rubble.
    public bool CaptureFracturePiecesInto(
        GeometryCellKey cell,
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (!_rubbleCells.TryGetValue(cell, out GeometryCellRecord? record))
        {
            return false;
        }

        AppendFracturePieces(record, pieces);
        return true;
    }

    private static void AppendFracturePieces(
        GeometryCellRecord record,
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> pieces)
    {
        if (record.FracturePieces == null)
        {
            return;
        }

        for (int index = 0; index < record.FracturePieces.Count; index++)
        {
            GeometryFracturePiece piece = record.FracturePieces[index];
            if (!piece.IsValid())
            {
                continue;
            }

            pieces.Add((record.Key, index, piece, record.Definition.MaterialId));
        }
    }

    private static int CountValidPieces(IReadOnlyList<GeometryFracturePiece> pieces)
    {
        int count = 0;
        for (int index = 0; index < pieces.Count; index++)
        {
            if (pieces[index].IsValid())
            {
                count++;
            }
        }

        return count;
    }
}
