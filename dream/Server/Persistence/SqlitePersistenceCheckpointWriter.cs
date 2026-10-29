using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;

namespace Dream.Server.Persistence;

// Writes a whole checkpoint in one owned transaction (IWorldStore.BeginOwnedTransaction: one
// connection, IMMEDIATE, never enlisted in a SqliteWriteBatch, since the final checkpoint runs as a
// write-queue read). Each table's SQL lives in its repository's *.Checkpoint.cs partial, which
// prepares one command per table and sets its parameters per row. Per-row repository calls would
// each open a connection and autocommit, which stalled the server tick for about 0.5 s every
// checkpoint.
//
// Player world state and the profile vitals are also written by the leave path (through the write
// queue), so those rows carry their capture time: a world-state row only replaces a stored row
// that is strictly older, and vitals are skipped once the stored world state is newer than their
// capture (see SqlitePlayerWorldStateRepository.CheckpointUpsert and
// SqlitePlayerProfileRepository.CheckpointUpdateVitals). Enemy, cube and rubble rows are
// update-only and stamped with the write time.
public sealed class SqlitePersistenceCheckpointWriter : IPersistenceCheckpointWriter
{
    private readonly IWorldStore _store;
    private readonly WorldId _worldId;

    // Bound to one world: every statement is scoped to its world_id.
    public SqlitePersistenceCheckpointWriter(SqliteSchemaBootstrapper schema, WorldId worldId)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _store = new SqliteWorldStore(schema);
        _worldId = worldId;
    }

    // World 1: the default world.
    public SqlitePersistenceCheckpointWriter(SqliteSchemaBootstrapper schema)
        : this(schema, WorldId.Default)
    {
    }

    public void Write(PersistenceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        // Nothing to write: open no connection and take no write lock.
        if (checkpoint.RowCount == 0 && checkpoint.WorldTimeTicks is null)
        {
            return;
        }

        string writeUtc = SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow);
        using SqliteSession session = _store.BeginOwnedTransaction();

        SqlitePlayerProfileRepository.CheckpointUpdateVitals(session, _worldId, checkpoint.PlayerVitals, writeUtc);
        SqlitePlayerWorldStateRepository.CheckpointUpsert(session, _worldId, checkpoint.PlayerWorldStates);
        SqliteEnemyRepository.CheckpointUpdate(session, _worldId, checkpoint.Enemies, writeUtc);
        SqlitePlacedObjectRepository.CheckpointUpdate(session, _worldId, checkpoint.PlacedCubes, writeUtc);
        SqliteRubbleRepository.CheckpointUpdate(session, _worldId, checkpoint.FracturePieces, writeUtc);
        SqliteWorldMetadataRepository.WriteWorldTime(session, _worldId, checkpoint.WorldTimeTicks);

        session.Commit();
    }
}

// SqliteSchemaBootstrapper.FormatUtc of per-row capture times, formatting each distinct time once:
// the rows of one capture share its time, so a checkpoint costs one string per capture rather than
// one per row. A local, mutable helper; use it as a local variable.
internal struct CheckpointUtcText
{
    private DateTime _utc;
    private string? _text;

    public string Format(DateTime utc)
    {
        if (_text == null || utc != _utc || utc.Kind != _utc.Kind)
        {
            _text = SqliteSchemaBootstrapper.FormatUtc(utc);
            _utc = utc;
        }

        return _text;
    }
}
