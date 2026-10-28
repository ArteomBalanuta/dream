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

    private static readonly PhysicsCubeMoveDelta[] NoMovedCubes = [];

    private readonly Dictionary<RigidBody, int> _rejectionStreaks = new();

    // The center last reported (in a PhysicsCubeMoveDelta) for each live dynamic cube. Moves are
    // measured from it rather than from the previous tick's center, so a cube creeping less than
    // the movement threshold per tick is still reported once its accumulated drift passes the
    // threshold. A cube's first sync seeds it with the center its body already stores, which
    // the placement that created the cube replicated. Entries for cubes that no longer have a
    // dynamic body (removed, fractured) are pruned.
    private readonly Dictionary<Cube, Vector3> _lastReportedCenters = new();

    // Per-call scratch state, cleared on each sync so steady-state ticks allocate nothing here.
    private readonly List<RigidBody> _bodies = [];
    private readonly List<PendingCubeSync> _pendingCubeSyncs = [];
    private readonly List<PendingCubeSync> _cubeRetryBuffer = [];
    private readonly List<PendingFracturePieceSync> _pendingFracturePieceSyncs = [];
    private readonly List<PhysicsCubeMoveDelta> _movedCubes = [];
    private readonly HashSet<GeometryCellKey> _reservedTargetCells = [];
    private readonly Dictionary<GeometryCellKey, List<PendingFracturePieceSync>> _fractureSyncsByCell = [];
    private readonly List<GeometryCellKey> _fractureCellOrder = [];
    private readonly Stack<List<PendingFracturePieceSync>> _fractureSyncListPool = new();
    private readonly List<(int PieceIndex, Vector3 Center)> _fracturePieceUpdates = [];
    private readonly HashSet<int> _acceptedPieceIndices = [];
    private readonly HashSet<RigidBody> _liveBodies = [];
    private readonly List<RigidBody> _staleStreakBodies = [];
    private readonly HashSet<Cube> _liveCubes = [];
    private readonly List<Cube> _staleBaselineCubes = [];

    // Activity tracking for the integrator's body set (see CollectBodiesThatCanHaveMoved).
    private InsertionOrderedSet<RigidBody>? _trackedBodySet;
    private long _syncedAddSequence;
    private long _syncedRemovedCount;
    private readonly HashSet<RigidBody> _revisitBodies = new(ReferenceEqualityComparer.Instance);
    private readonly List<int> _collectedSlots = [];
    private readonly List<(int Slot, RigidBody Body)> _revisitSlots = [];
    private readonly List<RigidBody> _mergedBodies = [];
    private readonly List<int> _mergedSlots = [];

    // Number of cubes with a last-reported center; bounded by the live dynamic cube bodies after
    // each sync.
    public int TrackedCubeBaselineCount => _lastReportedCenters.Count;

    // Bodies the last sync read and processed, and bodies it skipped as unable to have moved.
    public int LastSyncedBodyCount { get; private set; }

    public int LastSkippedBodyCount { get; private set; }

    // The returned MovedCubes belongs to the caller and stays valid across later syncs: it is a
    // new array when cubes moved and a shared empty array otherwise.
    public PhysicsSyncResult SyncDynamicMapBodies(Map map, IReadOnlyList<RigidBody> dynamicBodies)
    {
        // Snapshot the bodies: motion-state callbacks and map updates can change the live list.
        List<RigidBody> bodies = _bodies;
        bodies.Clear();
        InsertionOrderedSet<RigidBody>? bodySet = dynamicBodies as InsertionOrderedSet<RigidBody>;
        bool bodiesLeftSet = false;
        if (bodySet != null)
        {
            bodiesLeftSet = CollectBodiesThatCanHaveMoved(bodySet, bodies);
        }
        else
        {
            StopTrackingBodySet();
            for (int i = 0; i < dynamicBodies.Count; i++)
            {
                bodies.Add(dynamicBodies[i]);
            }
        }

        LastSyncedBodyCount = bodies.Count;
        LastSkippedBodyCount = dynamicBodies.Count - bodies.Count;

        List<PendingCubeSync> pendingCubeSyncs = _pendingCubeSyncs;
        List<PendingFracturePieceSync> pendingFracturePieceSyncs = _pendingFracturePieceSyncs;
        List<PhysicsCubeMoveDelta> movedCubes = _movedCubes;
        pendingCubeSyncs.Clear();
        pendingFracturePieceSyncs.Clear();
        movedCubes.Clear();

        for (int i = 0; i < bodies.Count; i++)
        {
            RigidBody body = bodies[i];
            // A type pattern, not the positional (Cube, Vector3) pattern: on object the positional
            // form goes through ITuple and boxes the Vector3 on every check.
            if (body.UserObject is ValueTuple<Cube, Vector3> cubeBody && cubeBody.Item1 is Cube cube)
            {
                body.MotionState.GetWorldTransform(out Matrix transform);
                _lastReportedCenters.TryAdd(cube, cubeBody.Item2);
                pendingCubeSyncs.Add(new PendingCubeSync(
                    body,
                    cube,
                    cubeBody.Item2,
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

        if (bodySet == null)
        {
            PruneRejectionStreaks(bodies);
            PruneLastReportedCenters(pendingCubeSyncs);
        }
        else if (bodiesLeftSet)
        {
            // State goes stale only when a body leaves the set, so the set path prunes only then.
            PruneRejectionStreaks(bodySet);
            PruneLastReportedCenters(bodySet);
        }

        HashSet<GeometryCellKey> reservedTargetCells = _reservedTargetCells;
        reservedTargetCells.Clear();

        // Multi-pass commit: a cube whose target cell is still owned by a cube that moves away
        // later in the same tick is retried after that cube has moved. A single ordered pass
        // rejected the trailing cubes of every pushed row. Swaps remain conservatively rejected.
        // The pending and retry lists swap roles after each pass.
        List<PendingCubeSync> remaining = pendingCubeSyncs;
        List<PendingCubeSync> retry = _cubeRetryBuffer;
        bool progressed = true;
        while (remaining.Count > 0 && progressed)
        {
            progressed = false;
            retry.Clear();
            for (int i = 0; i < remaining.Count; i++)
            {
                PendingCubeSync pendingSync = remaining[i];
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
                if (!AreBitEqual(pendingSync.PreviousCenter, pendingSync.Position))
                {
                    pendingSync.Body.UserObject = (pendingSync.Cube, pendingSync.Position);
                }

                if (hadPreviousCell &&
                    map.TryGetCellForCube(pendingSync.Cube, out GeometryCellKey currentCell) &&
                    (currentCell != previousCell ||
                        HasMeaningfulMovement(_lastReportedCenters[pendingSync.Cube], pendingSync.Position)))
                {
                    _lastReportedCenters[pendingSync.Cube] = pendingSync.Position;
                    movedCubes.Add(new PhysicsCubeMoveDelta(
                        pendingSync.Cube,
                        new Vector3(previousCell.X, previousCell.Y, previousCell.Z),
                        new Vector3(currentCell.X, currentCell.Y, currentCell.Z),
                        pendingSync.Position));
                }
            }

            (remaining, retry) = (retry, remaining);
        }

        for (int i = 0; i < remaining.Count; i++)
        {
            PendingCubeSync rejected = remaining[i];
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

        SyncFracturePieces(map, pendingFracturePieceSyncs);
        if (bodySet != null)
        {
            RememberBodiesToRevisit();
        }

        ReportInvariantViolationsInDebugBuilds(map, movedCubes.Count);
        return new PhysicsSyncResult(movedCubes.Count == 0 ? NoMovedCubes : movedCubes.ToArray());
    }

    // Groups fracture-piece syncs by cell in first-seen order, as GroupBy did, and lets the map
    // accept or reject each cell's pieces together.
    private void SyncFracturePieces(Map map, List<PendingFracturePieceSync> pendingFracturePieceSyncs)
    {
        _fractureSyncsByCell.Clear();
        _fractureCellOrder.Clear();
        for (int i = 0; i < pendingFracturePieceSyncs.Count; i++)
        {
            PendingFracturePieceSync pendingSync = pendingFracturePieceSyncs[i];
            if (!_fractureSyncsByCell.TryGetValue(pendingSync.CellKey, out List<PendingFracturePieceSync>? cellSyncs))
            {
                cellSyncs = _fractureSyncListPool.Count > 0 ? _fractureSyncListPool.Pop() : [];
                _fractureSyncsByCell.Add(pendingSync.CellKey, cellSyncs);
                _fractureCellOrder.Add(pendingSync.CellKey);
            }

            cellSyncs.Add(pendingSync);
        }

        for (int cellIndex = 0; cellIndex < _fractureCellOrder.Count; cellIndex++)
        {
            GeometryCellKey cell = _fractureCellOrder[cellIndex];
            List<PendingFracturePieceSync> cellSyncs = _fractureSyncsByCell[cell];
            _fracturePieceUpdates.Clear();
            for (int i = 0; i < cellSyncs.Count; i++)
            {
                _fracturePieceUpdates.Add((cellSyncs[i].PieceIndex, cellSyncs[i].Center));
            }

            bool accepted = map.UpdateFracturePiecePositions(
                cell,
                _fracturePieceUpdates,
                _acceptedPieceIndices);
            for (int i = 0; i < cellSyncs.Count; i++)
            {
                PendingFracturePieceSync pendingSync = cellSyncs[i];
                if (accepted && _acceptedPieceIndices.Contains(pendingSync.PieceIndex))
                {
                    if (!AreBitEqual(pendingSync.PreviousCenter, pendingSync.Center))
                    {
                        pendingSync.Body.UserObject = new GeometryDynamicBodyReference(
                            pendingSync.CellKey,
                            pendingSync.PieceIndex,
                            pendingSync.Center);
                    }
                }
                else
                {
                    ResetBodyToCenter(pendingSync.Body, pendingSync.PreviousCenter);
                }
            }

            cellSyncs.Clear();
            _fractureSyncListPool.Push(cellSyncs);
        }

        _fractureSyncsByCell.Clear();
        _fractureCellOrder.Clear();
        _fracturePieceUpdates.Clear();
        _acceptedPieceIndices.Clear();
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

    // Fills `bodies`, in set order, with the bodies whose motion state can differ from what the
    // last sync saw, and returns whether any body left the set since then. Bullet changes a
    // body's motion state only in synchronizeMotionStates, for bodies active after the step: an
    // island put to sleep in a step is put to sleep in buildIslands, before integrateTransforms,
    // and is neither integrated nor synchronized in that step. So a body that is not active now
    // has the transform the last sync read and the map already holds, and syncing it again
    // changes nothing. Every body this code teleports (ResetBodyToCenter,
    // WorldPhysicsBinder.TryMoveCompatibilityCubeBody) is activated in the same call. Collected:
    //   - bodies Bullet reports active (awake, or wanting deactivation but still integrated);
    //   - bodies added since the last sync, so each is synced at least once (it seeds its
    //     last-reported center there);
    //   - bodies with a rejection streak (the streak advances every sync until the body is
    //     accepted or reset, whether or not it sleeps).
    // Each is processed exactly as the full pass would, in the same relative order; the skipped
    // sleeping bodies would only have been re-accepted in place with no effect. So the result is
    // identical to syncing every body. Finding the active ones still reads one native flag per
    // body; everything after that is proportional to the collected bodies.
    private bool CollectBodiesThatCanHaveMoved(InsertionOrderedSet<RigidBody> set, List<RigidBody> bodies)
    {
        bool newSet = !ReferenceEquals(set, _trackedBodySet);
        if (newSet)
        {
            StopTrackingBodySet();
            _trackedBodySet = set;
        }

        long syncedAddSequence = _syncedAddSequence;
        List<int> slots = _collectedSlots;
        slots.Clear();
        int slotCount = set.SlotCount;
        for (int slot = 0; slot < slotCount; slot++)
        {
            RigidBody? body = set.SlotAt(slot, out long sequence);
            if (body != null && (sequence >= syncedAddSequence || body.IsActive))
            {
                bodies.Add(body);
                slots.Add(slot);
            }
        }

        if (_revisitBodies.Count > 0)
        {
            MergeRevisitBodies(set, syncedAddSequence, bodies, slots);
        }

        _syncedAddSequence = set.NextSequence;
        bool bodiesLeft = newSet || set.RemovedCount != _syncedRemovedCount;
        _syncedRemovedCount = set.RemovedCount;
        return bodiesLeft;
    }

    private void MergeRevisitBodies(
        InsertionOrderedSet<RigidBody> set,
        long syncedAddSequence,
        List<RigidBody> bodies,
        List<int> slots)
    {
        List<(int Slot, RigidBody Body)> revisit = _revisitSlots;
        revisit.Clear();
        foreach (RigidBody body in _revisitBodies)
        {
            // Bodies the scan already took (new or active) are not added twice.
            if (set.TryGetSlot(body, out int slot) &&
                set.SlotAt(slot, out long sequence) != null &&
                sequence < syncedAddSequence &&
                !body.IsActive)
            {
                revisit.Add((slot, body));
            }
        }

        _revisitBodies.Clear();
        if (revisit.Count == 0)
        {
            return;
        }

        revisit.Sort(static (left, right) => left.Slot.CompareTo(right.Slot));
        List<RigidBody> mergedBodies = _mergedBodies;
        List<int> mergedSlots = _mergedSlots;
        mergedBodies.Clear();
        mergedSlots.Clear();
        int scanned = 0;
        int revisited = 0;
        while (scanned < bodies.Count || revisited < revisit.Count)
        {
            if (revisited >= revisit.Count ||
                (scanned < bodies.Count && slots[scanned] < revisit[revisited].Slot))
            {
                mergedBodies.Add(bodies[scanned]);
                mergedSlots.Add(slots[scanned]);
                scanned++;
            }
            else
            {
                mergedBodies.Add(revisit[revisited].Body);
                mergedSlots.Add(revisit[revisited].Slot);
                revisited++;
            }
        }

        bodies.Clear();
        bodies.AddRange(mergedBodies);
        slots.Clear();
        slots.AddRange(mergedSlots);
        mergedBodies.Clear();
        mergedSlots.Clear();
        revisit.Clear();
    }

    private void RememberBodiesToRevisit()
    {
        _revisitBodies.Clear();
        foreach (RigidBody body in _rejectionStreaks.Keys)
        {
            _revisitBodies.Add(body);
        }
    }

    private void StopTrackingBodySet()
    {
        _trackedBodySet = null;
        _syncedAddSequence = long.MinValue;
        _syncedRemovedCount = -1;
        _revisitBodies.Clear();
    }

    private void PruneRejectionStreaks(InsertionOrderedSet<RigidBody> set)
    {
        if (_rejectionStreaks.Count == 0)
        {
            return;
        }

        _staleStreakBodies.Clear();
        foreach (RigidBody body in _rejectionStreaks.Keys)
        {
            if (!set.Contains(body))
            {
                _staleStreakBodies.Add(body);
            }
        }

        for (int i = 0; i < _staleStreakBodies.Count; i++)
        {
            _rejectionStreaks.Remove(_staleStreakBodies[i]);
        }

        _staleStreakBodies.Clear();
    }

    // Runs only after a body left the set: drops the baselines of cubes no body in the set
    // belongs to any more, exactly as the full pass does every sync.
    private void PruneLastReportedCenters(InsertionOrderedSet<RigidBody> set)
    {
        if (_lastReportedCenters.Count == 0)
        {
            return;
        }

        HashSet<Cube> live = _liveCubes;
        live.Clear();
        foreach (RigidBody body in set)
        {
            if (body.UserObject is ValueTuple<Cube, Vector3> cubeBody && cubeBody.Item1 is Cube cube)
            {
                live.Add(cube);
            }
        }

        _staleBaselineCubes.Clear();
        foreach (Cube cube in _lastReportedCenters.Keys)
        {
            if (!live.Contains(cube))
            {
                _staleBaselineCubes.Add(cube);
            }
        }

        for (int i = 0; i < _staleBaselineCubes.Count; i++)
        {
            _lastReportedCenters.Remove(_staleBaselineCubes[i]);
        }

        _staleBaselineCubes.Clear();
        live.Clear();
    }

    private void PruneRejectionStreaks(List<RigidBody> liveBodies)
    {
        if (_rejectionStreaks.Count == 0)
        {
            return;
        }

        HashSet<RigidBody> live = _liveBodies;
        live.Clear();
        for (int i = 0; i < liveBodies.Count; i++)
        {
            live.Add(liveBodies[i]);
        }

        _staleStreakBodies.Clear();
        foreach (RigidBody body in _rejectionStreaks.Keys)
        {
            if (!live.Contains(body))
            {
                _staleStreakBodies.Add(body);
            }
        }

        for (int i = 0; i < _staleStreakBodies.Count; i++)
        {
            _rejectionStreaks.Remove(_staleStreakBodies[i]);
        }

        _staleStreakBodies.Clear();
        live.Clear();
    }

    // Every cube synced this call has a baseline, so the dictionary holds stale cubes only when
    // it is larger than this call's cube count; the scan runs only then.
    private void PruneLastReportedCenters(List<PendingCubeSync> pendingCubeSyncs)
    {
        if (_lastReportedCenters.Count <= pendingCubeSyncs.Count)
        {
            return;
        }

        HashSet<Cube> live = _liveCubes;
        live.Clear();
        for (int i = 0; i < pendingCubeSyncs.Count; i++)
        {
            live.Add(pendingCubeSyncs[i].Cube);
        }

        _staleBaselineCubes.Clear();
        foreach (Cube cube in _lastReportedCenters.Keys)
        {
            if (!live.Contains(cube))
            {
                _staleBaselineCubes.Add(cube);
            }
        }

        for (int i = 0; i < _staleBaselineCubes.Count; i++)
        {
            _lastReportedCenters.Remove(_staleBaselineCubes[i]);
        }

        _staleBaselineCubes.Clear();
        live.Clear();
    }

    // Skipping the UserObject write when the stored center is bit-identical avoids re-boxing the
    // tuple or reference for every resting body; readers observe the same value either way.
    private static bool AreBitEqual(Vector3 left, Vector3 right)
    {
        return BitConverter.SingleToInt32Bits(left.X) == BitConverter.SingleToInt32Bits(right.X) &&
            BitConverter.SingleToInt32Bits(left.Y) == BitConverter.SingleToInt32Bits(right.Y) &&
            BitConverter.SingleToInt32Bits(left.Z) == BitConverter.SingleToInt32Bits(right.Z);
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
