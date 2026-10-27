namespace Dream.Server.Persistence;

// Checkpoint writes may run after the tick thread has already deleted or re-saved rows, so
// writers must update existing cube, fracture-piece and enemy rows only (never re-insert one),
// and touch only PlayerCheckpointVitals fields on profiles. Player world state is checkpoint-owned
// and may be upserted.
public interface IPersistenceCheckpointWriter
{
    void Write(PersistenceCheckpoint checkpoint);
}
