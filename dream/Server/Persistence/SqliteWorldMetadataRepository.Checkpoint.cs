using Dream.Server.Persistence.Store;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

// The checkpoint write of the world clock, run by SqlitePersistenceCheckpointWriter inside its
// owned transaction.
public sealed partial class SqliteWorldMetadataRepository
{
    internal const string WriteWorldTimeSql = """
        INSERT INTO world_metadata (key, value) VALUES ($key, $value)
        ON CONFLICT(key) DO UPDATE SET value = excluded.value;
        """;

    // The world clock rides in the same transaction as the rows it was captured with, so a
    // restored world never pairs one checkpoint's rows with another's time. Upsert is safe:
    // only the checkpoint writes this key. Null: leave the stored time as it is.
    internal static void WriteWorldTime(SqliteSession session, long? worldTimeTicks)
    {
        if (worldTimeTicks is not { } ticks)
        {
            return;
        }

        using SqliteCommand command = session.CreateCommand(WriteWorldTimeSql);
        command.Parameters.AddWithValue("$key", WorldTimeTicksKey);
        command.Parameters.AddWithValue("$value", FormatWorldTimeTicks(ticks));
        command.ExecuteNonQuery();
    }
}
