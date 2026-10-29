using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Enemies;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Persistence;

// Persistence checkpoints: full and incremental checkpoint capture, the final checkpoint written on
// dispose, and the checkpoint scratch state.
internal sealed partial class RuntimePersistenceBridge
{
    // Synchronous full capture-and-write, after the immediate writes it updates have committed.
    // The dedicated server instead captures under its lock and writes on a background worker;
    // see DedicatedServerHost.
    public void PersistCheckpoint()
    {
        PersistenceCheckpoint? checkpoint = CapturePersistenceCheckpoint(full: true);
        if (checkpoint != null && _checkpointWriter != null)
        {
            WaitForImmediateWrites(checkpoint);
            _checkpointWriter.Write(checkpoint);
        }
    }

    // Incremental checkpoints (SCALE-4): every capture but each FullCheckpointInterval-th holds only
    // what changed since the previous capture, which the map and the world-object registry track
    // (cube moves, registrations, damage and removals; rubble changes) and the capture diffs
    // (enemies, against what it last captured). Players are few and their rows also change
    // through profile saves, so connected players are captured every time. A full sweep repairs any
    // drift (a failed write, a change no hook saw); the first capture, and the first after a map
    // rebuild, is always full because nothing was tracked before it.
    public const int DefaultFullCheckpointInterval = 12;

    // Captures per full sweep: 12 at the server's 5-second cadence is one sweep a minute. 1 makes
    // every capture full.
    public int FullCheckpointInterval { get; set; } = DefaultFullCheckpointInterval;

    public long FullCheckpointCaptures { get; private set; }

    public long IncrementalCheckpointCaptures { get; private set; }

    // Copies the volatile authoritative state without touching the database, so it is cheap
    // enough to run on the tick thread. Null without persistence, or when an incremental capture
    // finds nothing to write (an idle world then allocates nothing). Every non-null capture also
    // carries the world clock (WorldTimeTicks); the full sweep about once a minute and the
    // shutdown checkpoint bound what a crash loses of world time.
    public PersistenceCheckpoint? CapturePersistenceCheckpoint(bool full)
    {
        if (_worldPersistence == null)
        {
            return null;
        }

        Map map = _host.Map;
        if (!ReferenceEquals(_checkpointTrackedMap, map) || !_worldObjects.IsCheckpointTrackingEnabled)
        {
            map.EnableCheckpointTracking();
            _worldObjects.EnableCheckpointTracking();
            _checkpointTrackedMap = map;
            full = true;
        }

        if (++_checkpointCapturesSinceFull >= Math.Max(1, FullCheckpointInterval))
        {
            full = true;
        }

        DateTime now = DateTime.UtcNow;
        PersistenceCheckpoint? checkpoint = full
            ? CaptureFullCheckpoint(map, now)
            : CaptureIncrementalCheckpoint(map, now);
        map.ClearCheckpointChanges();
        _worldObjects.ClearCheckpointChanges();
        if (full)
        {
            _checkpointCapturesSinceFull = 0;
            FullCheckpointCaptures++;
        }
        else
        {
            IncrementalCheckpointCaptures++;
        }

        return checkpoint;
    }

    // Writes a final full checkpoint on shutdown, behind the immediate writes still queued, so a
    // clean stop loses nothing since the last periodic checkpoint. Failures are logged only.
    // Tests turn it off to model a crash: the process dies without the shutdown flush.
    public bool WriteCheckpointOnDispose { get; set; } = true;

    // With a background queue the final checkpoint is queued as a read behind the immediate
    // writes, so it runs after them without a barrier wait on this thread, and under the queue's
    // one shutdown drain deadline: on a locked database it is dropped with them when the deadline
    // passes instead of adding its own barrier timeout and busy_timeout before the drain starts.
    // A healthy backlog that merely outlasts the deadline must not cost the final checkpoint,
    // though: the read is queued last, so it is the first thing such a drain drops. The returned
    // PendingFinalCheckpoint then writes it on this thread once the queue has stopped (still after every
    // immediate write that ran), unless the drain saw a lock episode. The caller disposes the
    // write queue between this call and PendingFinalCheckpoint.WriteIfDropped.
    public PendingFinalCheckpoint? WriteFinalCheckpoint()
    {
        if (!WriteCheckpointOnDispose || _worldPersistence == null || _checkpointWriter == null ||
            _writes is { IsClosed: true })
        {
            return null;
        }

        try
        {
            PersistenceCheckpoint? checkpoint = CapturePersistenceCheckpoint(full: true);
            if (checkpoint == null)
            {
                return null;
            }

            IPersistenceCheckpointWriter writer = _checkpointWriter;
            if (_writes is { IsBackground: true } writes)
            {
                writes.BeginShutdownDrain();
                PendingFinalCheckpoint queued = new(writer, checkpoint);
                if (writes.EnqueueRead(queued.WriteFromQueue))
                {
                    return queued;
                }
            }

            writer.Write(checkpoint);
        }
        catch (Exception ex)
        {
            PendingFinalCheckpoint.LogError(ex);
        }

        return null;
    }

    private static void WaitForImmediateWrites(PersistenceCheckpoint checkpoint)
    {
        try
        {
            checkpoint.ImmediateWritesCommitted?.Wait(PersistenceCheckpoint.ImmediateWritesWaitTimeout);
        }
        catch (AggregateException)
        {
        }
    }

    // Completes when every immediate write queued so far has committed (a read queued behind them).
    // Null when the queue runs writes inline (they have already committed) or is closed.
    private Task? CreateImmediateWritesBarrier()
    {
        if (_writes == null || !_writes.IsBackground)
        {
            return null;
        }

        TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _writes.EnqueueRead(() => committed.TrySetResult()) ? committed.Task : null;
    }

    private PersistenceCheckpoint CaptureFullCheckpoint(Map map, DateTime now)
    {
        CaptureConnectedPlayers(now, out PersistedPlayerWorldState[] worldStates, out PlayerCheckpointVitals[] vitals);

        _checkpointEnemies.Clear();
        List<PersistedEnemyInstance> enemies = _checkpointEnemyScratch;
        enemies.Clear();
        IReadOnlyList<EnemyActor> activeEnemies = _enemies.ActiveActors;
        for (int i = 0; i < activeEnemies.Count; i++)
        {
            EnemyActor enemy = activeEnemies[i];
            if (!enemy.IsActive)
            {
                continue;
            }

            PersistedEnemyInstance captured = CreatePersistedEnemy(enemy, now);
            enemies.Add(captured);
            _checkpointEnemies[ShimActorId(captured.ObjectId)] = captured;
        }

        List<PersistedPlacedCube> cubes = _checkpointCubeScratch;
        cubes.Clear();
        foreach (WorldObjectRecord record in _worldObjects.Records)
        {
            TryCaptureCube(map, record, now, cubes);
        }

        List<PersistedCubeFracturePiece> pieces = _checkpointPieceScratch;
        pieces.Clear();
        if (_persistedFractureGroups.Count > 0)
        {
            // One map scan for all groups instead of one full scan per fractured cell.
            _checkpointMapPieceScratch.Clear();
            map.CaptureFracturePiecesInto(_checkpointMapPieceScratch);
            AppendPersistedFracturePieces(_checkpointMapPieceScratch, now, pieces);
        }

        PersistenceCheckpoint checkpoint = new(worldStates, vitals, ToArray(enemies), ToArray(cubes), ToArray(pieces))
        {
            WorldTimeTicks = _host.WorldTicks,
            ImmediateWritesCommitted = CreateImmediateWritesBarrier()
        };
        ClearCheckpointScratch();
        return checkpoint;
    }

    private PersistenceCheckpoint? CaptureIncrementalCheckpoint(Map map, DateTime now)
    {
        CaptureConnectedPlayers(now, out PersistedPlayerWorldState[] worldStates, out PlayerCheckpointVitals[] vitals);

        // Enemies: diffed against the last capture, ignoring timestamps; one that is gone (killed
        // and pruned) is a tombstone.
        List<PersistedEnemyInstance> enemies = _checkpointEnemyScratch;
        enemies.Clear();
        HashSet<int> seenEnemies = _checkpointSeenEnemyIds;
        seenEnemies.Clear();
        IReadOnlyList<EnemyActor> activeEnemies = _enemies.ActiveActors;
        for (int i = 0; i < activeEnemies.Count; i++)
        {
            EnemyActor enemy = activeEnemies[i];
            if (!enemy.IsActive)
            {
                continue;
            }

            PersistedEnemyInstance captured = CreatePersistedEnemy(enemy, now);
            int capturedActorId = ShimActorId(captured.ObjectId);
            seenEnemies.Add(capturedActorId);
            if (_checkpointEnemies.TryGetValue(capturedActorId, out PersistedEnemyInstance previous) &&
                captured with { CreatedUtc = previous.CreatedUtc, UpdatedUtc = previous.UpdatedUtc } == previous)
            {
                continue;
            }

            enemies.Add(captured);
            _checkpointEnemies[capturedActorId] = captured;
        }

        List<int> removedEnemies = _checkpointRemovedEnemyScratch;
        removedEnemies.Clear();
        if (seenEnemies.Count != _checkpointEnemies.Count)
        {
            foreach (int enemyId in _checkpointEnemies.Keys)
            {
                if (!seenEnemies.Contains(enemyId))
                {
                    removedEnemies.Add(enemyId);
                }
            }

            for (int i = 0; i < removedEnemies.Count; i++)
            {
                _checkpointEnemies.Remove(removedEnemies[i]);
            }
        }

        // Cubes: registered or damaged (registry) and moved (map); removed ids are tombstones.
        List<PersistedPlacedCube> cubes = _checkpointCubeScratch;
        cubes.Clear();
        HashSet<Cube> dirtyCubes = _worldObjects.CheckpointDirtyCubes;
        foreach (Cube cube in dirtyCubes)
        {
            TryCaptureCube(map, cube, now, cubes);
        }

        foreach (Cube cube in map.CheckpointMovedCubes)
        {
            if (!dirtyCubes.Contains(cube))
            {
                TryCaptureCube(map, cube, now, cubes);
            }
        }

        // Fracture pieces: every piece of each changed rubble cell that belongs to a persisted
        // group; a group whose rubble is gone is a tombstone.
        List<PersistedCubeFracturePiece> pieces = _checkpointPieceScratch;
        pieces.Clear();
        List<ObjectId> removedGroups = _checkpointRemovedGroupScratch;
        removedGroups.Clear();
        foreach (GeometryCellKey cell in map.CheckpointDirtyRubbleCells)
        {
            if (!_persistedFractureGroups.TryGetValue(cell, out PersistedFractureGroup group))
            {
                continue;
            }

            _checkpointMapPieceScratch.Clear();
            map.CaptureFracturePiecesInto(cell, _checkpointMapPieceScratch);
            if (_checkpointMapPieceScratch.Count == 0)
            {
                removedGroups.Add(group.Origin);
                continue;
            }

            AppendPersistedFracturePieces(_checkpointMapPieceScratch, now, pieces);
        }

        HashSet<int> removedCubes = _worldObjects.CheckpointRemovedCubeIds;
        if (worldStates.Length == 0 && enemies.Count == 0 && removedEnemies.Count == 0 && cubes.Count == 0 &&
            removedCubes.Count == 0 && pieces.Count == 0 && removedGroups.Count == 0)
        {
            ClearCheckpointScratch();
            return null;
        }

        PersistenceCheckpoint checkpoint = new(worldStates, vitals, ToArray(enemies), ToArray(cubes), ToArray(pieces))
        {
            IsIncremental = true,
            // C0 shim: tombstones carry ObjectId(actorId).
            RemovedCubeObjectIds = ToObjectIds(removedCubes),
            RemovedEnemyObjectIds = ToObjectIds(removedEnemies),
            RemovedFractureOriginObjectIds = ToArray(removedGroups),
            WorldTimeTicks = _host.WorldTicks,
            ImmediateWritesCommitted = CreateImmediateWritesBarrier()
        };
        ClearCheckpointScratch();
        return checkpoint;
    }

    private void CaptureConnectedPlayers(
        DateTime now,
        out PersistedPlayerWorldState[] worldStates,
        out PlayerCheckpointVitals[] vitals)
    {
        List<PersistedPlayerWorldState> states = _checkpointPlayerStateScratch;
        List<PlayerCheckpointVitals> playerVitals = _checkpointPlayerVitalsScratch;
        states.Clear();
        playerVitals.Clear();
        foreach (ConnectedPlayer player in _host.Sessions.Players)
        {
            if (player.IsLocal ||
                !_host.TryGetPlayerCheckpointState(
                    player.SessionId,
                    out Vector3 position,
                    out Vector3 facing,
                    out int currentHealth,
                    out int maxHealth))
            {
                continue;
            }

            if (_departedWorldStates.Count > 0)
            {
                // Another session under this name is attached, so its state is now the newer one.
                _departedWorldStates.Remove(player.DisplayName);
            }

            // Both rows carry the capture time, which guards them against a leave that runs after
            // this capture but is saved before the checkpoint is written (see PlayerCheckpointVitals).
            playerVitals.Add(new PlayerCheckpointVitals(
                player.DisplayName,
                _host.GetSessionInteractionMode(player.SessionId),
                currentHealth,
                maxHealth)
            {
                CapturedUtc = now
            });
            states.Add(new PersistedPlayerWorldState(
                player.DisplayName,
                position,
                facing,
                CurrentHealth: currentHealth,
                CurrentEnergy: 0,
                UpdatedUtc: now));
        }

        worldStates = ToArray(states);
        vitals = ToArray(playerVitals);
    }

    private void TryCaptureCube(Map map, Cube cube, DateTime now, List<PersistedPlacedCube> cubes)
    {
        if (_worldObjects.TryGet(cube) is { } record)
        {
            TryCaptureCube(map, record, now, cubes);
        }
    }

    // The previous per-cube checkpoint path repaired identities from the database, reloading
    // the whole placed-cube table and querying profiles once per cube on the tick thread. The
    // capture uses registry identity only; records without a persistence owner are left to the
    // placement/combat paths that still repair identity before writing.
    private void TryCaptureCube(Map map, WorldObjectRecord record, DateTime now, List<PersistedPlacedCube> cubes)
    {
        if (record.SourceObject is not Cube cube ||
            string.IsNullOrWhiteSpace(record.OwnerPlayerName) ||
            !map.TryGetCellForCube(cube, out GeometryCellKey cell) ||
            cell.Y == 0)
        {
            return;
        }

        GeometryCellRecord? geometryCell = map.TryGetGeometryCellAtCell(cell.X, cell.Y, cell.Z);
        PersistedPlacedCube captured = new(
            new ObjectId(record.Actor.Id),
            record.OwnerPlayerName,
            new Vector3(cell.X, cell.Y, cell.Z),
            GeometryShapeKind.SolidCube,
            geometryCell?.Definition.MaterialId ?? GeometryMaterialId.Grass,
            record.Health.CurrentHealth,
            record.Health.MaxHealth,
            now,
            now,
            cube.Position);
        cubes.Add(captured);
        int capturedCubeId = record.Actor.Id;
        if (_persistedCubes.ContainsKey(capturedCubeId))
        {
            _persistedCubes[capturedCubeId] = captured;
        }
    }

    private void AppendPersistedFracturePieces(
        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> mapPieces,
        DateTime now,
        List<PersistedCubeFracturePiece> pieces)
    {
        for (int i = 0; i < mapPieces.Count; i++)
        {
            (GeometryCellKey pieceCell, int pieceIndex, GeometryFracturePiece piece, GeometryMaterialId materialId) = mapPieces[i];
            if (!_persistedFractureGroups.TryGetValue(pieceCell, out PersistedFractureGroup group))
            {
                continue;
            }

            pieces.Add(new PersistedCubeFracturePiece(
                ObjectId: ObjectId.None,
                OriginObjectId: group.Origin,
                PieceIndex: pieceIndex,
                OwnerPlayerName: group.OwnerPlayerName,
                ParentCell: new Vector3(pieceCell.X, pieceCell.Y, pieceCell.Z),
                Center: piece.Center,
                SideLength: piece.SideLength,
                MaterialId: materialId,
                CreatedUtc: now,
                UpdatedUtc: now));
        }
    }

    private void ClearCheckpointScratch()
    {
        _checkpointEnemyScratch.Clear();
        _checkpointCubeScratch.Clear();
        _checkpointPieceScratch.Clear();
        _checkpointMapPieceScratch.Clear();
        _checkpointPlayerStateScratch.Clear();
        _checkpointPlayerVitalsScratch.Clear();
        _checkpointRemovedEnemyScratch.Clear();
        _checkpointRemovedGroupScratch.Clear();
    }

    private static T[] ToArray<T>(List<T> items)
    {
        return items.Count == 0 ? [] : items.ToArray();
    }

    // C0 shim: actor ids as ObjectIds.
    private static ObjectId[] ToObjectIds(IReadOnlyCollection<int> actorIds)
    {
        if (actorIds.Count == 0)
        {
            return [];
        }

        ObjectId[] result = new ObjectId[actorIds.Count];
        int index = 0;
        foreach (int actorId in actorIds)
        {
            result[index++] = new ObjectId(actorId);
        }

        return result;
    }

    // Checkpoint capture state. The scratch lists are reused across captures (a capture copies them
    // into exact-size arrays), so a capture of k changed rows allocates O(k) once they have grown.
    private Map? _checkpointTrackedMap;
    private int _checkpointCapturesSinceFull;
    private readonly Dictionary<int, PersistedEnemyInstance> _checkpointEnemies = [];
    private readonly HashSet<int> _checkpointSeenEnemyIds = [];
    private readonly List<PersistedEnemyInstance> _checkpointEnemyScratch = [];
    private readonly List<int> _checkpointRemovedEnemyScratch = [];
    private readonly List<PersistedPlacedCube> _checkpointCubeScratch = [];
    private readonly List<PersistedCubeFracturePiece> _checkpointPieceScratch = [];
    private readonly List<ObjectId> _checkpointRemovedGroupScratch = [];
    private readonly List<PersistedPlayerWorldState> _checkpointPlayerStateScratch = [];
    private readonly List<PlayerCheckpointVitals> _checkpointPlayerVitalsScratch = [];
    private readonly List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)>
        _checkpointMapPieceScratch = [];
}

// The final checkpoint queued as a read (RuntimePersistenceBridge.WriteFinalCheckpoint). Whichever
// of the queue worker (WriteFromQueue) and the dispose thread (WriteIfDropped, after the queue
// stopped) claims it first writes it, once.
internal sealed class PendingFinalCheckpoint(IPersistenceCheckpointWriter writer, PersistenceCheckpoint checkpoint)
{
    private int _claimed;

    public void WriteFromQueue()
    {
        if (Interlocked.Exchange(ref _claimed, 1) == 0)
        {
            Write(writer, checkpoint);
        }
    }

    // Called after PersistenceWriteQueue.Dispose returned, so the worker has stopped: an
    // unclaimed checkpoint was dropped by the drain deadline. On a database the drain saw
    // locked it stays dropped (shutdown stays bounded by the deadline); otherwise the backlog
    // was only slow, and the checkpoint gets its attempt here.
    public void WriteIfDropped(PersistenceWriteQueue? writes)
    {
        if (writes is { LockSeenDuringShutdownDrain: true } ||
            Interlocked.Exchange(ref _claimed, 1) != 0)
        {
            return;
        }

        Dream.Multiplayer.Diagnostics.NetworkDiagnosticsLog.Write(
            "server.checkpoint.final-fallback",
            $"final checkpoint dropped by an unlocked drain; writing it after the queue stopped rows={checkpoint.RowCount}");
        Write(writer, checkpoint);
    }

    private static void Write(IPersistenceCheckpointWriter writer, PersistenceCheckpoint checkpoint)
    {
        try
        {
            writer.Write(checkpoint);
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    internal static void LogError(Exception ex)
    {
        Dream.Multiplayer.Diagnostics.NetworkDiagnosticsLog.Write(
            "server.checkpoint.final-error",
            $"{ex.GetType().Name}: {ex.Message}");
    }
}
