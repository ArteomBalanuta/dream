using OpenTK.Mathematics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Runtime;

// World snapshot capture and export.
public sealed partial class WorldSessionRuntime
{
    // The world for a join or resync: a WorldSnapshot over a pooled capture (CaptureWorldSnapshot).
    public WorldSnapshot ExportWorldSnapshot()
    {
        return CaptureWorldSnapshot().ToWorldSnapshot();
    }

    // SCALE-1: the join and resync world, captured on the tick into pooled flat arrays in one pass
    // over the map, with no LINQ and no per-cube objects. The compact encode runs later on the
    // connection writer, which releases the capture once it has written the last chunk.
    public WorldSnapshotCapture CaptureWorldSnapshot()
    {
        IReadOnlyDictionary<GeometryCellKey, GeometryCellRecord> geometryCells = Map.GeometryCells;
        IReadOnlyDictionary<GeometryCellKey, GeometryCellRecord> rubbleCells = Map.RubbleCells;
        WorldSnapshotCapture capture = WorldSnapshotCapture.Rent(
            Map.Width,
            Map.Height,
            Map.Depth,
            LastWorldMutationSequence,
            cubeCapacity: geometryCells.Count,
            subcellCapacity: 64,
            rubbleCapacity: rubbleCells.Count,
            pieceCapacity: rubbleCells.Count * 6);

        foreach (GeometryCellRecord cell in geometryCells.Values)
        {
            if (cell.Key.Y == 0)
            {
                continue;
            }

            if (cell.SubdivisionState != null)
            {
                capture.AddSubcell(cell.Key.X, cell.Key.Y, cell.Key.Z);
                continue;
            }

            Cube? cube = Map.TryGetCubeAtCell(cell.Key.X, cell.Key.Y, cell.Key.Z);
            Vector3 centerPosition = cube?.Position ??
                new Vector3(cell.Key.X + 0.5f, cell.Key.Y + 0.5f, cell.Key.Z + 0.5f);
            Vector3 authoritativeCell = ToCellPosition(centerPosition);
            capture.AddCube(
                (int)authoritativeCell.X,
                (int)authoritativeCell.Y,
                (int)authoritativeCell.Z,
                cell.Definition.MaterialId,
                cube != null ? _worldObjects.TryGet(cube)?.Actor.Id : null,
                centerPosition);
        }

        foreach (GeometryCellRecord rubble in rubbleCells.Values)
        {
            capture.BeginRubble(rubble.Key.X, rubble.Key.Y, rubble.Key.Z, rubble.Definition.MaterialId);
            IReadOnlyList<GeometryFracturePiece> pieces = rubble.FracturePieces!;
            for (int i = 0; i < pieces.Count; i++)
            {
                if (pieces[i].IsValid())
                {
                    capture.AddPiece(pieces[i]);
                }
            }

            capture.EndRubble();
        }

        return capture;
    }
}
