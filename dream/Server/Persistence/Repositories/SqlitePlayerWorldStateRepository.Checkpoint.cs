using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Repositories;

// The checkpoint write of player_world_state, run by SqlitePersistenceCheckpointWriter inside its
// owned transaction.
public sealed partial class SqlitePlayerWorldStateRepository
{
    // Insert-if-profile-exists upsert, guarded by capture time: each row carries the time it was
    // captured (PersistedPlayerWorldState.UpdatedUtc), and the update branch only replaces a stored
    // row that is strictly older. The leave path (RuntimePersistenceBridge.PersistPlayerWorldState)
    // also writes this table, through the write queue, stamping its worker time, which is never
    // earlier than a capture taken on the tick thread before the leave. So a checkpoint captured
    // before a leave but written after it no longer overwrites what the leave saved.
    // FormatUtc is fixed-width ("O", UTC), so comparing the text compares the times. A legacy
    // strftime value (3 fractional digits, then 'Z') compares as newer than any 7-digit value within
    // the same millisecond: the conservative direction (the checkpoint row is skipped).
    internal const string CheckpointUpsertSql = """
        INSERT INTO player_world_state (
            player_name, position_x, position_y, position_z,
            facing_x, facing_y, facing_z, current_hp, current_energy, updated_utc)
        SELECT
            $playerName, $positionX, $positionY, $positionZ,
            $facingX, $facingY, $facingZ, $currentHp, $currentEnergy, $updatedUtc
        WHERE EXISTS (SELECT 1 FROM player_profiles WHERE player_name = $playerName)
        ON CONFLICT(player_name) DO UPDATE SET
            position_x = excluded.position_x,
            position_y = excluded.position_y,
            position_z = excluded.position_z,
            facing_x = excluded.facing_x,
            facing_y = excluded.facing_y,
            facing_z = excluded.facing_z,
            current_hp = excluded.current_hp,
            current_energy = excluded.current_energy,
            updated_utc = excluded.updated_utc
        WHERE player_world_state.updated_utc < excluded.updated_utc;
        """;

    // One prepared command for all rows, its parameters set per row.
    internal static void CheckpointUpsert(SqliteSession session, IReadOnlyList<PersistedPlayerWorldState> states)
    {
        if (states.Count == 0)
        {
            return;
        }

        using SqliteCommand command = session.CreateCommand(CheckpointUpsertSql);
        SqliteParameter playerName = command.AddParameter("$playerName", SqliteType.Text);
        SqliteParameter positionX = command.AddParameter("$positionX", SqliteType.Real);
        SqliteParameter positionY = command.AddParameter("$positionY", SqliteType.Real);
        SqliteParameter positionZ = command.AddParameter("$positionZ", SqliteType.Real);
        SqliteParameter facingX = command.AddParameter("$facingX", SqliteType.Real);
        SqliteParameter facingY = command.AddParameter("$facingY", SqliteType.Real);
        SqliteParameter facingZ = command.AddParameter("$facingZ", SqliteType.Real);
        SqliteParameter currentHp = command.AddParameter("$currentHp", SqliteType.Integer);
        SqliteParameter currentEnergy = command.AddParameter("$currentEnergy", SqliteType.Integer);
        SqliteParameter updatedUtc = command.AddParameter("$updatedUtc", SqliteType.Text);

        // The rows of one capture share its time, so it is formatted once, not once per row.
        CheckpointUtcText capturedUtc = default;
        foreach (PersistedPlayerWorldState state in states)
        {
            playerName.Value = state.PlayerName.Trim();
            positionX.Value = state.Position.X;
            positionY.Value = state.Position.Y;
            positionZ.Value = state.Position.Z;
            facingX.Value = state.Facing.X;
            facingY.Value = state.Facing.Y;
            facingZ.Value = state.Facing.Z;
            currentHp.Value = state.CurrentHealth;
            currentEnergy.Value = state.CurrentEnergy;
            updatedUtc.Value = capturedUtc.Format(state.UpdatedUtc);
            command.ExecuteNonQuery();
        }
    }
}
