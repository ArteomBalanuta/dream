namespace Dream.Simulation.Persistence;

// Checkpoint writes may run after the tick thread has already deleted or re-saved rows, so
// writers must update existing cube, fracture-piece and enemy rows only (never re-insert one),
// and touch only PlayerCheckpointVitals fields on profiles. Player world state is checkpoint-owned
// and may be upserted. Full and incremental checkpoints are written the same way; an incremental
// checkpoint's tombstones are not deletes (the ordered PersistenceWriteQueue deletes rows, and a
// checkpoint delete could land after a re-insert of the same key), so writers ignore them.
public interface IPersistenceCheckpointWriter
{
    void Write(PersistenceCheckpoint checkpoint);
}
