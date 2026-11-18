using Dream.Server.Persistence.Store;
using Dream.Simulation.Content;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

// The global enemy_kinds table (migration 0017): per-kind chase speed, patrol speed, attack interval,
// attack range and aggro range, edited by hand in SQL and read once at boot. Rows are returned as
// stored; the caller validates them (EnemyKindTuning.IsValid) because a hand edit can be anything.
public sealed class SqliteEnemyKindRepository : SqliteRepositoryBase
{
    private const string LoadAllSql = """
        SELECT
            enemy_kind,
            chase_speed,
            patrol_speed,
            attack_interval_seconds,
            attack_range,
            aggro_range
        FROM enemy_kinds
        ORDER BY enemy_kind;
        """;

    public SqliteEnemyKindRepository(SqliteSchemaBootstrapper schema)
        : base(new SqliteWorldStore(schema))
    {
    }

    public IReadOnlyList<EnemyKindTuning> LoadAll()
    {
        return QueryList(LoadAllSql, ReadRow);
    }

    private static EnemyKindTuning ReadRow(SqliteDataReader reader)
    {
        return new EnemyKindTuning(
            reader.GetString(0),
            (float)reader.GetDouble(1),
            (float)reader.GetDouble(2),
            (float)reader.GetDouble(3),
            (float)reader.GetDouble(4),
            (float)reader.GetDouble(5));
    }
}
