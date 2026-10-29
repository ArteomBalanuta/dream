using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.Server.Persistence.Repositories;

// The hot checkpoint statements carry the world id and the write time as literals (a bound
// parameter is looked up and bound again on every row).
public sealed class CheckpointSqlLiteralsTests
{
    [Fact]
    public void WithCheckpointLiterals_ReplacesTheWorldIdAndTheWriteTime_AndNothingElse()
    {
        string sql = SqliteRepositorySql.WithCheckpointLiterals(
            SqlitePlacedObjectRepository.CheckpointUpdateSql, new WorldId(42), "2026-10-29T01:02:03.0000000Z");

        Assert.Contains("updated_utc = '2026-10-29T01:02:03.0000000Z'", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE world_id = 42 AND object_id = $objectId;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("$worldId", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("$updatedUtc", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void WithCheckpointLiterals_EscapesAQuoteInTheWriteTime()
    {
        string sql = SqliteRepositorySql.WithCheckpointLiterals(
            SqliteEnemyRepository.CheckpointUpdateSql, new WorldId(1), "x'; DROP TABLE enemy_instances; --");

        Assert.Contains("updated_utc = 'x''; DROP TABLE enemy_instances; --'", sql, StringComparison.Ordinal);
    }
}
