using Dream.Simulation.Persistence;

namespace Dream.Simulation.Runtime;

// Persistence checkpoints: the runtime's checkpoint API, delegating to RuntimePersistenceBridge,
// which owns full and incremental capture, the dirty-set use and the final checkpoint written on
// dispose (see Dispose for the shutdown order: final checkpoint, queue drain, dropped-final fallback).
public sealed partial class WorldSessionRuntime
{
    public IPersistenceCheckpointWriter? CheckpointWriter => _persistence.CheckpointWriter;

    // Synchronous full capture-and-write, after the immediate writes it updates have committed.
    // The dedicated server instead captures under its lock and writes on a background worker;
    // see DedicatedServerHost.
    public void PersistCheckpoint() => _persistence.PersistCheckpoint();

    // See RuntimePersistenceBridge.DefaultFullCheckpointInterval (SCALE-4 incremental checkpoints).
    public const int DefaultFullCheckpointInterval = RuntimePersistenceBridge.DefaultFullCheckpointInterval;

    // Captures per full sweep: 12 at the server's 5-second cadence is one sweep a minute. 1 makes
    // every capture full.
    public int FullCheckpointInterval
    {
        get => _persistence.FullCheckpointInterval;
        set => _persistence.FullCheckpointInterval = value;
    }

    public long FullCheckpointCaptures => _persistence.FullCheckpointCaptures;

    public long IncrementalCheckpointCaptures => _persistence.IncrementalCheckpointCaptures;

    // Copies the volatile authoritative state without touching the database, so it is cheap
    // enough to run on the tick thread. Null without persistence, or when an incremental capture
    // finds nothing to write (an idle world then allocates nothing).
    public PersistenceCheckpoint? CapturePersistenceCheckpoint()
    {
        return _persistence.CapturePersistenceCheckpoint(full: false);
    }

    public PersistenceCheckpoint? CapturePersistenceCheckpoint(bool full)
    {
        return _persistence.CapturePersistenceCheckpoint(full);
    }

    // Writes a final full checkpoint on shutdown, behind the immediate writes still queued, so a
    // clean stop loses nothing since the last periodic checkpoint. Failures are logged only.
    // Tests turn it off to model a crash: the process dies without the shutdown flush.
    internal bool WriteCheckpointOnDispose
    {
        get => _persistence.WriteCheckpointOnDispose;
        set => _persistence.WriteCheckpointOnDispose = value;
    }

    // See RuntimePersistenceBridge.WriteFinalCheckpoint; Dispose then drains the queue and calls
    // PendingFinalCheckpoint.WriteIfDropped.
    private PendingFinalCheckpoint? WriteFinalCheckpoint() => _persistence.WriteFinalCheckpoint();
}
