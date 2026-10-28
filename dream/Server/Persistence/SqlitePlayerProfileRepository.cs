using Dream.Simulation.Players;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

public sealed class SqlitePlayerProfileRepository : IPlayerProfileRepository, IDisposable
{
    private readonly SqliteSchemaBootstrapper _schema;

    public SqlitePlayerProfileRepository(SqliteSchemaBootstrapper schema)
    {
        _schema = schema;
    }

    public PersistedPlayerProfile GetOrCreate(string playerName)
    {
        PersistedPlayerProfile? existing = TryGet(playerName);
        if (existing.HasValue)
        {
            return existing.Value;
        }

        DateTime now = DateTime.UtcNow;
        PersistedPlayerProfile created = new(
            playerName.Trim(),
            playerName.Trim(),
            PlayerInteractionMode.Building,
            new PersistedCreatureStats(
                CurrentHealth: 100,
                MaxHealth: 100,
                Strength: 10,
                Agility: 10,
                Vitality: 10,
                Energy: 10,
                MovementSpeed: 6f,
                AttackSpeed: 2f,
                Level: 1,
                Experience: 0),
            now,
            now);
        Save(created);
        return created;
    }

    public PersistedPlayerProfile? TryGet(string playerName)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                player_name,
                display_name,
                interaction_mode,
                strength,
                agility,
                vitality,
                energy,
                movement_speed,
                attack_speed,
                level,
                experience,
                current_hp,
                max_hp,
                created_utc,
                updated_utc
            FROM player_profiles
            WHERE player_name = $playerName;
            """;
        command.Parameters.AddWithValue("$playerName", playerName.Trim());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return ReadProfile(reader);
    }

    public void Save(PersistedPlayerProfile profile)
    {
        DateTime now = DateTime.UtcNow;
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO player_profiles (
                player_name,
                display_name,
                interaction_mode,
                strength,
                agility,
                vitality,
                energy,
                movement_speed,
                attack_speed,
                level,
                experience,
                current_hp,
                max_hp,
                created_utc,
                updated_utc
            )
            VALUES (
                $playerName,
                $displayName,
                $interactionMode,
                $strength,
                $agility,
                $vitality,
                $energy,
                $movementSpeed,
                $attackSpeed,
                $level,
                $experience,
                $currentHealth,
                $maxHealth,
                $createdUtc,
                $updatedUtc
            )
            ON CONFLICT(player_name) DO UPDATE SET
                display_name = excluded.display_name,
                interaction_mode = excluded.interaction_mode,
                strength = excluded.strength,
                agility = excluded.agility,
                vitality = excluded.vitality,
                energy = excluded.energy,
                movement_speed = excluded.movement_speed,
                attack_speed = excluded.attack_speed,
                level = excluded.level,
                experience = excluded.experience,
                current_hp = excluded.current_hp,
                max_hp = excluded.max_hp,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$playerName", profile.PlayerName.Trim());
        command.Parameters.AddWithValue(
            "$displayName",
            string.IsNullOrWhiteSpace(profile.DisplayName)
                ? profile.PlayerName.Trim()
                : profile.DisplayName.Trim());
        command.Parameters.AddWithValue("$interactionMode", profile.InteractionMode.ToString());
        command.Parameters.AddWithValue("$strength", profile.Stats.Strength);
        command.Parameters.AddWithValue("$agility", profile.Stats.Agility);
        command.Parameters.AddWithValue("$vitality", profile.Stats.Vitality);
        command.Parameters.AddWithValue("$energy", profile.Stats.Energy);
        command.Parameters.AddWithValue("$movementSpeed", profile.Stats.MovementSpeed);
        command.Parameters.AddWithValue("$attackSpeed", profile.Stats.AttackSpeed);
        command.Parameters.AddWithValue("$level", profile.Stats.Level);
        command.Parameters.AddWithValue("$experience", profile.Stats.Experience);
        command.Parameters.AddWithValue("$currentHealth", profile.Stats.CurrentHealth);
        command.Parameters.AddWithValue("$maxHealth", profile.Stats.MaxHealth);
        command.Parameters.AddWithValue("$createdUtc", SqliteSchemaBootstrapper.FormatUtc(profile.CreatedUtc == default ? now : profile.CreatedUtc));
        command.Parameters.AddWithValue("$updatedUtc", SqliteSchemaBootstrapper.FormatUtc(now));
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
    }

    private static PersistedPlayerProfile ReadProfile(SqliteDataReader reader)
    {
        return new PersistedPlayerProfile(
            reader.GetString(0),
            reader.GetString(1),
            Enum.Parse<PlayerInteractionMode>(reader.GetString(2), ignoreCase: true),
            new PersistedCreatureStats(
                CurrentHealth: reader.GetInt32(11),
                MaxHealth: reader.GetInt32(12),
                Strength: reader.GetInt32(3),
                Agility: reader.GetInt32(4),
                Vitality: reader.GetInt32(5),
                Energy: reader.GetInt32(6),
                MovementSpeed: reader.GetFloat(7),
                AttackSpeed: reader.GetFloat(8),
                Level: reader.GetInt32(9),
                Experience: reader.GetInt32(10)),
            SqliteSchemaBootstrapper.ParseUtc(reader.GetString(13)),
            SqliteSchemaBootstrapper.ParseUtc(reader.GetString(14)));
    }
}
