using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Physics;


public class PhysicsSyncSystem
{
    // A body whose cell transition is rejected keeps its physics position for this many
    // consecutive ticks before it is reset to the accepted map center. Resetting on the first
    // rejection teleported pushed cubes back every tick (reject/reset ping-pong), zeroed their
    // velocity and kept them awake; most rejections are transient penetration under a push and
    // resolve within a few ticks.
    public const int RejectionsBeforeReset = 8;

    private readonly Dictionary<RigidBody, int> _rejectionStreaks = new();

    public PhysicsSyncResult SyncDynamicMapBodies(Map map, IEnumerable<RigidBody> dynamicBodies)
    {
        RigidBody[] bodies = dynamicBodies as RigidBody[] ?? dynamicBodies.ToArray();
        List<PendingCubeSync> pendingCubeSyncs = [];
        List<PendingFracturePieceSync> pendingFracturePieceSyncs = [];
        List<PhysicsCubeMoveDelta> movedCubes = [];

        foreach (RigidBody body in bodies)
        {
            if (body.UserObject is (Cube cube, Vector3 previousCenter))
            {
                body.MotionState.GetWorldTransform(out Matrix transform);
                pendingCubeSyncs.Add(new PendingCubeSync(
                    body,
                    cube,
                    previousCenter,
                    transform.Origin.ToOpenTK()));
                continue;
            }

            if (body.UserObject is GeometryDynamicBodyReference dynamicGeometry)
            {
                body.MotionState.GetWorldTransform(out Matrix transform);
                pendingFracturePieceSyncs.Add(new PendingFracturePieceSync(
                    body,
                    dynamicGeometry.CellKey,
                    dynamicGeometry.PieceIndex,
                    dynamicGeometry.InitialCenter,
                    transform.Origin.ToOpenTK()));
            }
        }

        PruneRejectionStreaks(bodies);
        HashSet<GeometryCellKey> reservedTargetCells = [];

        // Multi-pass commit: a cube whose target cell is still owned by a cube that moves away
        // later in the same tick is retried after that cube has moved. A single ordered pass
        // rejected the trailing cubes of every pushed row. Swaps remain conservatively rejected.
        List<PendingCubeSync> remaining = pendingCubeSyncs;
        bool progressed = true;
        while (remaining.Count > 0 && progressed)
        {
            progressed = false;
            List<PendingCubeSync> retry = [];
            foreach (PendingCubeSync pendingSync in remaining)
            {
                bool hadPreviousCell = map.TryGetCellForCube(pendingSync.Cube, out GeometryCellKey previousCell);
                if (!map.UpdateCubePosition(
                        pendingSync.Cube,
                        pendingSync.Position,
                        reservedTargetCells,
                        syncPhysics: false))
                {
                    retry.Add(pendingSync);
                    continue;
                }

                progressed = true;
                _rejectionStreaks.Remove(pendingSync.Body);
                pendingSync.Body.UserObject = (pendingSync.Cube, pendingSync.Position);
                if (hadPreviousCell &&
                    map.TryGetCellForCube(pendingSync.Cube, out GeometryCellKey currentCell) &&
                    HasMeaningfulMovement(pendingSync.PreviousCenter, pendingSync.Position))
                {
                    movedCubes.Add(new PhysicsCubeMoveDelta(
                        pendingSync.Cube,
                        new Vector3(previousCell.X, previousCell.Y, previousCell.Z),
                        new Vector3(currentCell.X, currentCell.Y, currentCell.Z),
                        pendingSync.Position));
                }
            }

            remaining = retry;
        }

        foreach (PendingCubeSync rejected in remaining)
        {
            int streak = _rejectionStreaks.TryGetValue(rejected.Body, out int previous) ? previous + 1 : 1;
            if (streak < RejectionsBeforeReset)
            {
                // Leave the body where physics put it; the map keeps the accepted center.
                _rejectionStreaks[rejected.Body] = streak;
                continue;
            }

            _rejectionStreaks.Remove(rejected.Body);
            Vector3 acceptedCenter = rejected.Cube.Position;
            rejected.Body.UserObject = (rejected.Cube, acceptedCenter);
            ResetBodyToCenter(rejected.Body, acceptedCenter);
        }

        foreach (IGrouping<GeometryCellKey, PendingFracturePieceSync> cellGroup in pendingFracturePieceSyncs.GroupBy(sync => sync.CellKey))
        {
            PendingFracturePieceSync[] cellSyncs = cellGroup.ToArray();
            bool accepted = map.UpdateFracturePiecePositions(
                cellGroup.Key,
                cellSyncs
                    .Select(sync => (sync.PieceIndex, sync.Center))
                    .ToArray(),
                out HashSet<int> acceptedPieceIndices);
            foreach (PendingFracturePieceSync pendingSync in cellSyncs)
            {
                if (accepted && acceptedPieceIndices.Contains(pendingSync.PieceIndex))
                {
                    pendingSync.Body.UserObject = new GeometryDynamicBodyReference(
                        pendingSync.CellKey,
                        pendingSync.PieceIndex,
                        pendingSync.Center);
                }
                else
                {
                    ResetBodyToCenter(pendingSync.Body, pendingSync.PreviousCenter);
                }
            }
        }

        ReportInvariantViolationsInDebugBuilds(map, movedCubes.Count);
        return new PhysicsSyncResult(movedCubes);
    }

    // Debug builds verify the map's parallel indexes after any tick that moved a cube, so a
    // broken mutation shows up where it happened instead of later as an invisible or duplicated cube.
    [System.Diagnostics.Conditional("DEBUG")]
    private static void ReportInvariantViolationsInDebugBuilds(Map map, int movedCubeCount)
    {
        if (movedCubeCount == 0)
        {
            return;
        }

        IReadOnlyList<string> violations = map.FindInvariantViolations();
        if (violations.Count == 0)
        {
            return;
        }

        string detail = string.Join("; ", violations.Take(5));
        System.Diagnostics.Debug.WriteLine($"[map.invariant] {violations.Count} violation(s): {detail}");
        Dream.Multiplayer.Diagnostics.NetworkDiagnosticsLog.Write("map.invariant", $"count={violations.Count} {detail}");
    }

    private void PruneRejectionStreaks(RigidBody[] liveBodies)
    {
        if (_rejectionStreaks.Count == 0)
        {
            return;
        }

        HashSet<RigidBody> live = [.. liveBodies];
        foreach (RigidBody stale in _rejectionStreaks.Keys.Where(body => !live.Contains(body)).ToArray())
        {
            _rejectionStreaks.Remove(stale);
        }
    }

    private static bool HasMeaningfulMovement(Vector3 previous, Vector3 current)
    {
        return (previous - current).LengthSquared > 0.000001f;
    }

    private static void ResetBodyToCenter(RigidBody body, Vector3 center)
    {
        Matrix transform = Matrix.Translation(center.ToBullet());
        body.WorldTransform = transform;
        body.MotionState?.SetWorldTransform(ref transform);
        body.LinearVelocity = BulletSharp.Math.Vector3.Zero;
        body.AngularVelocity = BulletSharp.Math.Vector3.Zero;
        body.Activate(true);
    }

    private readonly record struct PendingCubeSync(
        RigidBody Body,
        Cube Cube,
        Vector3 PreviousCenter,
        Vector3 Position);

    private readonly record struct PendingFracturePieceSync(
        RigidBody Body,
        GeometryCellKey CellKey,
        int PieceIndex,
        Vector3 PreviousCenter,
        Vector3 Center);
}

public readonly record struct PhysicsCubeMoveDelta(
    Cube Cube,
    Vector3 SourceCellPosition,
    Vector3 TargetCellPosition,
    Vector3 CenterPosition);

public readonly record struct PhysicsSyncResult(
    IReadOnlyList<PhysicsCubeMoveDelta> MovedCubes);
