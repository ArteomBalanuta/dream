using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

public sealed partial class SqlitePlayerProfileRepository
    : SqliteRepositoryBase, IPlayerProfileRepository, IDisposable
{
    private const string TryGetSql = """
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
            updated_utc,
            stat_points
        FROM player_profiles
        WHERE player_name = $playerName;
        """;

    private const string SaveSql = """
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
            updated_utc,
            stat_points
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
            $updatedUtc,
            $statPoints
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
            updated_utc = excluded.updated_utc,
            stat_points = excluded.stat_points;
        """;

    public SqlitePlayerProfileRepository(SqliteSchemaBootstrapper schema)
        : base(new SqliteWorldStore(schema))
    {
    }

    public PersistedPlayerProfile GetOrCreate(string playerName)
    {
        PersistedPlayerProfile? existing = TryGet(playerName);
        if (existing.HasValue)
        {
            return existing.Value;
        }

        PersistedPlayerProfile created = PersistedPlayerProfile.CreateDefault(playerName, DateTime.UtcNow);
        Save(created);
        return created;
    }

    public PersistedPlayerProfile? TryGet(string playerName)
    {
        return QuerySingle(
            TryGetSql,
            playerName,
            static (command, name) => command.BindText("$playerName", name.Trim()),
            ReadProfile);
    }

    public void Save(PersistedPlayerProfile profile)
    {
        // The stamp the caller gave is stored as it is: the runtime stamps a profile when it queues the
        // save, so a restore can tell a row written by another world after that from a stale one.
        DateTime now = profile.UpdatedUtc == default ? DateTime.UtcNow : profile.UpdatedUtc;
        Execute(SaveSql, (profile, now), static (command, state) => BindProfile(command, state.profile, state.now));
    }

    public void Dispose()
    {
    }

    private static void BindProfile(SqliteCommand command, PersistedPlayerProfile profile, DateTime now)
    {
        string playerName = profile.PlayerName.Trim();
        command.BindText("$playerName", playerName);
        command.BindText(
            "$displayName",
            string.IsNullOrWhiteSpace(profile.DisplayName)
                ? playerName
                : profile.DisplayName.Trim());
        command.BindText("$interactionMode", profile.InteractionMode.ToString());
        command.BindInt64("$strength", profile.Stats.Strength);
        command.BindInt64("$agility", profile.Stats.Agility);
        command.BindInt64("$vitality", profile.Stats.Vitality);
        command.BindInt64("$energy", profile.Stats.Energy);
        command.BindReal("$movementSpeed", profile.Stats.MovementSpeed);
        command.BindReal("$attackSpeed", profile.Stats.AttackSpeed);
        command.BindInt64("$level", profile.Stats.Level);
        command.BindInt64("$experience", profile.Stats.Experience);
        command.BindInt64("$currentHealth", profile.Stats.CurrentHealth);
        command.BindInt64("$maxHealth", profile.Stats.MaxHealth);
        command.BindUtc("$createdUtc", profile.CreatedUtc == default ? now : profile.CreatedUtc);
        command.BindUtc("$updatedUtc", now);
        command.BindInt64("$statPoints", Math.Max(0, profile.Stats.StatPoints));
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
                Experience: reader.GetInt32(10),
                StatPoints: reader.GetInt32(15)),
            reader.GetUtc(13),
            reader.GetUtc(14));
    }
}
