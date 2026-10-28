using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

public partial class Map
{
    public bool IsCheckpointTrackingEnabled => _checkpointMovedCubes != null;

    public void EnableCheckpointTracking()
    {
        _checkpointMovedCubes ??= new HashSet<Cube>(ReferenceEqualityComparer.Instance);
        _checkpointDirtyRubbleCells ??= [];
    }

    // Cubes whose position changed since ClearCheckpointChanges; empty while tracking is off.
    // Concrete sets (read-only by contract), so the capture enumerates them without boxing.
    internal HashSet<Cube> CheckpointMovedCubes => _checkpointMovedCubes ?? EmptyCubeSet;

    // Rubble cells changed since ClearCheckpointChanges; empty while tracking is off.
    internal HashSet<GeometryCellKey> CheckpointDirtyRubbleCells => _checkpointDirtyRubbleCells ?? EmptyCellSet;

    public void ClearCheckpointChanges()
    {
        _checkpointMovedCubes?.Clear();
        _checkpointDirtyRubbleCells?.Clear();
    }

    private static readonly HashSet<Cube> EmptyCubeSet = [];
    private static readonly HashSet<GeometryCellKey> EmptyCellSet = [];
}
