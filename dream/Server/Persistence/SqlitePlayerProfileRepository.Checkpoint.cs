using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

// The checkpoint write of the profile vitals, run by SqlitePersistenceCheckpointWriter inside its
// owned transaction.
public sealed partial class SqlitePlayerProfileRepository
{
    // Only the PlayerCheckpointVitals fields; level, experience and base stats are saved when they
    // change. Stamped with the write time, as before the guard.
    //
    // The guard (PlayerCheckpointVitals.CapturedUtc set): the vitals are skipped when the player's
    // player_world_state row is strictly newer than the capture. Only the leave
    // (RuntimePersistenceBridge.PersistPlayerWorldState, through the write queue, stamped with the
    // leave's tick-thread time) and checkpoints (stamped with their capture time) write that row, so
    // a newer row means the player left after this capture, and the profile the leave saved must
    // stand. That needs the leave to queue its world state BEFORE its profile
    // (WorldSessionRuntime.DetachRemoteSession): a checkpoint written between the two then skips.
    //
    // Why not the profile's own updated_utc: a profile save (XP, level, stats) moves it too, so a save
    // queued before a capture but run after it could make the capture's vitals skip, depending only on
    // how far the queue worker lagged the tick. (Immediate saves now store the time they were queued,
    // RuntimePersistenceBridge.SaveProfile, but a checkpoint's own vitals update stamps the row too.) Two servers
    // running the same script would then persist different HP. Strict > keeps this independent of
    // whether the world-state upsert of the same checkpoint already ran (it writes the capture time).
    // A NULL $capturedUtc (the default CapturedUtc) makes the comparison NULL, so the row is
    // unguarded. FormatUtc is fixed-width ("O", UTC), so comparing the text compares the times; a
    // legacy strftime value (3 fractional digits, then 'Z') compares as newer than any 7-digit value
    // within the same millisecond: the conservative direction (the vitals are skipped).
    internal const string CheckpointUpdateVitalsSql = """
        UPDATE player_profiles SET
            interaction_mode = $interactionMode,
            current_hp = $currentHp,
            max_hp = $maxHp,
            updated_utc = $updatedUtc
        WHERE player_name = $playerName
            AND NOT EXISTS (
                SELECT 1 FROM player_world_state
                WHERE player_world_state.world_id = $worldId
                    AND player_world_state.player_name = $playerName
                    AND player_world_state.updated_utc > $capturedUtc);
        """;

    // One prepared command for all rows, its parameters set per row. writeUtc is the formatted
    // write time every updated profile is stamped with.
    internal static void CheckpointUpdateVitals(
        SqliteSession session,
        WorldId worldId,
        IReadOnlyList<PlayerCheckpointVitals> vitals,
        string writeUtc)
    {
        if (vitals.Count == 0)
        {
            return;
        }

        using SqliteCommand command = session.CreateCommand(CheckpointUpdateVitalsSql);
        SqliteParameter playerName = command.AddParameter("$playerName", SqliteType.Text);
        SqliteParameter interactionMode = command.AddParameter("$interactionMode", SqliteType.Text);
        SqliteParameter currentHp = command.AddParameter("$currentHp", SqliteType.Integer);
        SqliteParameter maxHp = command.AddParameter("$maxHp", SqliteType.Integer);
        SqliteParameter capturedUtc = command.AddParameter("$capturedUtc", SqliteType.Text);
        command.Parameters.AddWithValue("$updatedUtc", writeUtc);
        command.Parameters.AddWithValue("$worldId", worldId.Value);

        // The rows of one capture share its time, so it is formatted once, not once per row.
        CheckpointUtcText capturedText = default;
        foreach (PlayerCheckpointVitals entry in vitals)
        {
            playerName.Value = entry.PlayerName.Trim();
            interactionMode.Value = entry.InteractionMode.ToString();
            currentHp.Value = entry.CurrentHealth;
            maxHp.Value = entry.MaxHealth;
            capturedUtc.Value = entry.CapturedUtc == default ? DBNull.Value : (object)capturedText.Format(entry.CapturedUtc);
            command.ExecuteNonQuery();
        }
    }
}
