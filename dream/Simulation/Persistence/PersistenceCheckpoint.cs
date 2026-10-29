using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Simulation.Persistence;

// In-memory copy of the volatile authoritative state saved every checkpoint. It is captured on
// the tick thread without any database access and written later by an IPersistenceCheckpointWriter.
//
// A full checkpoint (the positional constructor) holds every live row. An incremental one
// (IsIncremental) holds only the rows that changed since the previous capture, plus tombstones:
// the keys removed since then. Writers apply both kinds the same way (update existing rows only;
// see IPersistenceCheckpointWriter), so a tombstone is never a SQL delete: row deletes stay on the
// ordered PersistenceWriteQueue, which a re-insert of the same key is ordered behind. Tombstones
// matter when queued checkpoints are merged (Merge): they drop the older update of a removed key.
public sealed record PersistenceCheckpoint(
    IReadOnlyList<PersistedPlayerWorldState> PlayerWorldStates,
    IReadOnlyList<PlayerCheckpointVitals> PlayerVitals,
    IReadOnlyList<PersistedEnemyInstance> Enemies,
    IReadOnlyList<PersistedPlacedCube> PlacedCubes,
    IReadOnlyList<PersistedCubeFracturePiece> FracturePieces)
{
    public static PersistenceCheckpoint Empty { get; } = new([], [], [], [], []);

    // False for a full checkpoint, which supersedes everything captured before it.
    public bool IsIncremental { get; init; }

    // Placed cubes removed since the previous capture (and not re-added).
    public IReadOnlyList<ObjectId> RemovedCubeObjectIds { get; init; } = [];

    // Enemies removed since the previous capture.
    public IReadOnlyList<ObjectId> RemovedEnemyObjectIds { get; init; } = [];

    // Fracture groups (by origin ObjectId) whose rubble is gone since the previous capture.
    public IReadOnlyList<ObjectId> RemovedFractureOriginObjectIds { get; init; } = [];

    // The world clock (WorldClock.WorldTicks) at the capture; written to world_metadata in the
    // checkpoint's transaction. Null: leave the stored world time as it is.
    public long? WorldTimeTicks { get; init; }

    // Completes once every immediate write queued before the capture has RUN, whether it committed
    // or failed (see PersistenceWriteQueue.EnqueueRead): it is an ordering barrier, not a success
    // signal. The writer waits for it, so a checkpoint update never reaches the database ahead of
    // the insert of a row it updates. It never completes when the queue drops it (closed, or the
    // shutdown drain deadline passed); waiters are bounded by
    // ImmediateWritesWaitTimeout and, at shutdown, by the queue's drain deadline. Null: nothing to
    // wait for.
    public Task? ImmediateWritesCommitted { get; init; }

    // How long a write waits for a checkpoint's ImmediateWritesCommitted barrier before going ahead
    // anyway (the queue is closed, or stuck). BackgroundCheckpointWriter.ImmediateWritesWaitTimeout.
    public static readonly TimeSpan ImmediateWritesWaitTimeout = TimeSpan.FromSeconds(10);

    // Rows a writer updates for this checkpoint (tombstones excluded).
    public int RowCount =>
        PlayerWorldStates.Count + PlayerVitals.Count + Enemies.Count + PlacedCubes.Count + FracturePieces.Count;

    public int TombstoneCount =>
        RemovedCubeObjectIds.Count + RemovedEnemyObjectIds.Count + RemovedFractureOriginObjectIds.Count;

    // Folds a newer checkpoint into an older one that has not been written yet, so the merged write
    // leaves the database where writing both in order would. The last write wins per key: a newer
    // row replaces the older row and clears an older tombstone of that key (a delete followed by a
    // re-add keeps the re-add), and a newer tombstone drops the older row and is kept. Older rows
    // a newer full checkpoint does not hold are kept too: their keys were removed (the update is
    // then a no-op) or are no longer captured (a rubble group replaced in the same cell), and
    // dropping them would leave those rows at an older value than a sequential write. The result
    // is full when either input is, since every key changed after the older one is in the newer.
    // World time is a single value: the newer one wins, and the older one stands in when the newer
    // checkpoint has none.
    public static PersistenceCheckpoint Merge(PersistenceCheckpoint older, PersistenceCheckpoint newer)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);
        if (older.RowCount == 0 && older.TombstoneCount == 0)
        {
            return newer.WorldTimeTicks.HasValue || !older.WorldTimeTicks.HasValue
                ? newer
                : newer with { WorldTimeTicks = older.WorldTimeTicks };
        }

        StringComparer names = StringComparer.OrdinalIgnoreCase;
        (IReadOnlyList<PersistedPlacedCube> cubes, IReadOnlyList<ObjectId> removedCubes) = MergeKeyed(
            older.PlacedCubes,
            older.RemovedCubeObjectIds,
            newer.PlacedCubes,
            newer.RemovedCubeObjectIds,
            static cube => cube.ObjectId,
            EqualityComparer<ObjectId>.Default);
        (IReadOnlyList<PersistedEnemyInstance> enemies, IReadOnlyList<ObjectId> removedEnemies) = MergeKeyed(
            older.Enemies,
            older.RemovedEnemyObjectIds,
            newer.Enemies,
            newer.RemovedEnemyObjectIds,
            static enemy => enemy.ObjectId,
            EqualityComparer<ObjectId>.Default);
        (IReadOnlyList<PersistedPlayerWorldState> worldStates, _) = MergeKeyed(
            older.PlayerWorldStates,
            [],
            newer.PlayerWorldStates,
            [],
            static state => state.PlayerName.Trim(),
            names);
        (IReadOnlyList<PlayerCheckpointVitals> vitals, _) = MergeKeyed(
            older.PlayerVitals,
            [],
            newer.PlayerVitals,
            [],
            static entry => entry.PlayerName.Trim(),
            names);
        (IReadOnlyList<PersistedCubeFracturePiece> pieces, IReadOnlyList<ObjectId> removedGroups) = MergeFracturePieces(older, newer);

        return new PersistenceCheckpoint(worldStates, vitals, enemies, cubes, pieces)
        {
            IsIncremental = older.IsIncremental && newer.IsIncremental,
            RemovedCubeObjectIds = removedCubes,
            RemovedEnemyObjectIds = removedEnemies,
            RemovedFractureOriginObjectIds = removedGroups,
            WorldTimeTicks = newer.WorldTimeTicks ?? older.WorldTimeTicks,
            // The queue commits in order, so the newer barrier implies the older one.
            ImmediateWritesCommitted = newer.ImmediateWritesCommitted ?? older.ImmediateWritesCommitted
        };
    }

    private static (IReadOnlyList<TRow> Rows, IReadOnlyList<TKey> Removed) MergeKeyed<TRow, TKey>(
        IReadOnlyList<TRow> olderRows,
        IReadOnlyList<TKey> olderRemoved,
        IReadOnlyList<TRow> newerRows,
        IReadOnlyList<TKey> newerRemoved,
        Func<TRow, TKey> keyOf,
        IEqualityComparer<TKey> comparer)
        where TKey : notnull
    {
        if (olderRows.Count == 0 && olderRemoved.Count == 0)
        {
            return (newerRows, newerRemoved);
        }

        // Insertion order: older keys first (in their order), then keys new in the newer one.
        Dictionary<TKey, TRow> rows = new(olderRows.Count + newerRows.Count, comparer);
        List<TKey> order = new(olderRows.Count + newerRows.Count);
        HashSet<TKey> removed = new(olderRemoved, comparer);
        for (int i = 0; i < olderRows.Count; i++)
        {
            TKey key = keyOf(olderRows[i]);
            if (rows.TryAdd(key, olderRows[i]))
            {
                order.Add(key);
            }
            else
            {
                rows[key] = olderRows[i];
            }
        }

        for (int i = 0; i < newerRemoved.Count; i++)
        {
            rows.Remove(newerRemoved[i]);
            removed.Add(newerRemoved[i]);
        }

        for (int i = 0; i < newerRows.Count; i++)
        {
            TKey key = keyOf(newerRows[i]);
            removed.Remove(key);
            if (!rows.ContainsKey(key))
            {
                order.Add(key);
            }

            rows[key] = newerRows[i];
        }

        List<TRow> mergedRows = new(rows.Count);
        foreach (TKey key in order)
        {
            if (rows.Remove(key, out TRow? row))
            {
                mergedRows.Add(row);
            }
        }

        return (mergedRows, removed.ToArray());
    }

    // Pieces are keyed by their own ObjectId; their tombstones by origin ObjectId, since a group's
    // rubble goes as a whole. A piece with no ObjectId (None) falls back to (origin, piece index).
    private static (ObjectId Piece, ObjectId Origin, int Index) PieceKey(PersistedCubeFracturePiece piece) =>
        piece.ObjectId.IsNone ? (ObjectId.None, piece.OriginObjectId, piece.PieceIndex) : (piece.ObjectId, ObjectId.None, 0);

    private static (IReadOnlyList<PersistedCubeFracturePiece> Pieces, IReadOnlyList<ObjectId> Removed) MergeFracturePieces(
        PersistenceCheckpoint older,
        PersistenceCheckpoint newer)
    {
        if (older.FracturePieces.Count == 0 && older.RemovedFractureOriginObjectIds.Count == 0)
        {
            return (newer.FracturePieces, newer.RemovedFractureOriginObjectIds);
        }

        HashSet<ObjectId> newerRemoved = new(newer.RemovedFractureOriginObjectIds);
        HashSet<ObjectId> newerSources = [];
        HashSet<(ObjectId, ObjectId, int)> newerKeys = [];
        foreach (PersistedCubeFracturePiece piece in newer.FracturePieces)
        {
            newerSources.Add(piece.OriginObjectId);
            newerKeys.Add(PieceKey(piece));
        }

        List<PersistedCubeFracturePiece> pieces = new(older.FracturePieces.Count + newer.FracturePieces.Count);
        foreach (PersistedCubeFracturePiece piece in older.FracturePieces)
        {
            if (!newerRemoved.Contains(piece.OriginObjectId) && !newerKeys.Contains(PieceKey(piece)))
            {
                pieces.Add(piece);
            }
        }

        pieces.AddRange(newer.FracturePieces);

        HashSet<ObjectId> removed = new(older.RemovedFractureOriginObjectIds);
        removed.ExceptWith(newerSources);
        removed.UnionWith(newerRemoved);
        return (pieces, removed.ToArray());
    }
}

// Only the profile fields that change without an immediate save. Level, experience and base
// stats are saved when they change, so a late checkpoint write must not overwrite them.
public readonly record struct PlayerCheckpointVitals(
    string PlayerName,
    PlayerInteractionMode InteractionMode,
    int CurrentHealth,
    int MaxHealth)
{
    // When the vitals were captured (on the tick thread). A writer skips them when the player's
    // stored world state is strictly newer (the player left after this capture), so a checkpoint
    // captured before a leave never overwrites the profile the leave saved; see
    // IPersistenceCheckpointWriter. Merge keeps whole rows, so each row keeps its own time. The
    // default value means unguarded: the writer always overwrites, as before the guard.
    public DateTime CapturedUtc { get; init; }
}
