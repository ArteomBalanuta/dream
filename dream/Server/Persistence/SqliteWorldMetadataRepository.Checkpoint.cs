using Dream.Server.Persistence.Store;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

// The checkpoint write of the world clock, run by SqlitePersistenceCheckpointWriter inside its
// owned transaction.
public sealed partial class SqliteWorldMetadataRepository
{
    internal const string WriteWorldTimeSql = "UPDATE worlds SET world_time_ticks = $ticks WHERE world_id = $worldId;";

    // The world clock rides in the same transaction as the rows it was captured with, so a
    // restored world never pairs one checkpoint's rows with another's time. Only the checkpoint
    // writes this column. Null: leave the stored time as it is. A world without a row cannot be
    // checkpointed: that is a bug in the caller, so it throws instead of dropping the clock.
    internal static void WriteWorldTime(SqliteSession session, WorldId worldId, long? worldTimeTicks)
    {
        if (worldTimeTicks is not { } ticks)
        {
            return;
        }

        using SqliteCommand command = session.CreateCommand(WriteWorldTimeSql);
        command.Parameters.AddWithValue("$ticks", ticks);
        command.Parameters.AddWithValue("$worldId", worldId.Value);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"World {worldId} does not exist; its clock was not written.");
        }
    }
}
