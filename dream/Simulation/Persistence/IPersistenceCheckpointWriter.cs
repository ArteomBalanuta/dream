namespace Dream.Simulation.Persistence;

// Checkpoint writes may run after the tick thread has already deleted or re-saved rows, so
// writers must update existing cube, fracture-piece and enemy rows only (never re-insert one),
// and touch only PlayerCheckpointVitals fields on profiles. Player world state may be upserted,
// but the leave path also saves it (and the profile) through the write queue, so a checkpoint
// captured before a leave can be written after it. Player rows are therefore guarded by capture
// time: a world-state row (PersistedPlayerWorldState.UpdatedUtc) replaces a stored row only when
// the stored row is strictly older than the capture, and vitals (PlayerCheckpointVitals.CapturedUtc,
// when set) are skipped when the player's stored world state is strictly newer than the capture
// (only the leave and checkpoints write it; the profile's own time also moves with every immediate
// profile save). Full and incremental checkpoints are written the same way; an
// incremental checkpoint's tombstones are not deletes (the ordered PersistenceWriteQueue deletes
// rows, and a checkpoint delete could land after a re-insert of the same key), so writers ignore
// them.
public interface IPersistenceCheckpointWriter
{
    void Write(PersistenceCheckpoint checkpoint);
}
