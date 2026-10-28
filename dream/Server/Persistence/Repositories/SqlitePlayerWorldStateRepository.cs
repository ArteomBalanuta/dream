using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;

namespace Dream.Server.Persistence.Repositories;

// player_world_state: one row per player, written on leave (and by the checkpoint, in the
// Checkpoint partial).
public sealed partial class SqlitePlayerWorldStateRepository(IWorldStore store)
    : SqliteRepositoryBase(store), IPlayerWorldStateRepository
{
    private const string SelectSql = """
        SELECT
            player_name,
            position_x,
            position_y,
            position_z,
            facing_x,
            facing_y,
            facing_z,
            current_hp,
            current_energy,
            updated_utc
        FROM player_world_state
        WHERE player_name = $playerName;
        """;

    private const string UpsertSql = """
        INSERT INTO player_world_state (
            player_name,
            position_x,
            position_y,
            position_z,
            facing_x,
            facing_y,
            facing_z,
            current_hp,
            current_energy,
            updated_utc
        )
        VALUES (
            $playerName,
            $positionX,
            $positionY,
            $positionZ,
            $facingX,
            $facingY,
            $facingZ,
            $currentHp,
            $currentEnergy,
            $updatedUtc
        )
        ON CONFLICT(player_name) DO UPDATE SET
            position_x = excluded.position_x,
            position_y = excluded.position_y,
            position_z = excluded.position_z,
            facing_x = excluded.facing_x,
            facing_y = excluded.facing_y,
            facing_z = excluded.facing_z,
            current_hp = excluded.current_hp,
            current_energy = excluded.current_energy,
            updated_utc = excluded.updated_utc;
        """;

    public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName) =>
        QuerySingle(
            SelectSql,
            playerName,
            static (command, name) => command.BindText("$playerName", name.Trim()),
            static reader => new PersistedPlayerWorldState(
                reader.GetString(0),
                reader.GetVector3(1),
                reader.GetVector3(4),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetUtc(9)));

    // updated_utc is state.UpdatedUtc, the time the state was captured (the leave's time, or the
    // checkpoint's capture time through RepositoryPersistenceCheckpointWriter), so the checkpoint
    // guards compare capture times and not how late the write queue ran. A default UpdatedUtc is
    // stamped with the write time.
    public void SavePlayerWorldState(PersistedPlayerWorldState state) =>
        Execute(UpsertSql, state, static (command, s) =>
        {
            command.BindText("$playerName", s.PlayerName.Trim());
            command.BindVector3("$positionX", "$positionY", "$positionZ", s.Position);
            command.BindVector3("$facingX", "$facingY", "$facingZ", s.Facing);
            command.BindInt64("$currentHp", s.CurrentHealth);
            command.BindInt64("$currentEnergy", s.CurrentEnergy);
            command.BindUtc("$updatedUtc", s.UpdatedUtc == default ? DateTime.UtcNow : s.UpdatedUtc);
        });
}
