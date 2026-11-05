using Dream.Server.Persistence.Migrations;
using Dream.Simulation.Players;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// Migration 0015 moves characters stored on the 1000 attribute origin (commit a39702e6) onto the 10
// origin by subtracting 990 from any attribute at or above 1000, so PointsSpent is unchanged. Only rows
// created at or after a39702e6 (2026-11-04T19:44:37Z) move: before 1f422512 the ceiling was 1000 and the
// setters clamp, so an older character can sit at exactly 1000 having spent nothing, and creation time
// is the only thing that separates it from a 1000-origin character.
public sealed class AttributeOriginMigrationTests
{
    private static MigrationTestDatabase V14With(string values)
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(new SqliteSchemaMigrator([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(14)]));
        database.ExecuteScript(
            "INSERT INTO player_profiles (player_name, display_name, strength, agility, vitality, energy) VALUES " + values + ";");
        SqliteConnection.ClearAllPools();
        return database;
    }

    private static string Stats(MigrationTestDatabase database, string player)
    {
        using SqliteConnection connection = database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT strength || '/' || agility || '/' || vitality || '/' || energy FROM player_profiles WHERE player_name = '{player}';";
        return (string)command.ExecuteScalar()!;
    }

    [Fact]
    public void ANothingSpentCharacterOnTheOldOrigin_LandsOnTen()
    {
        using MigrationTestDatabase database = V14With("('hero', 'Hero', 1000, 1000, 1000, 1000)");

        database.Schema.EnsureSchema();

        Assert.Equal("10/10/10/10", Stats(database, "hero"));
    }

    [Fact]
    public void RealSpends_ArePreserved_AsThePointsSpentIdentity()
    {
        using MigrationTestDatabase database = V14With("('hero', 'Hero', 1005, 1000, 1012, 1000)");

        database.Schema.EnsureSchema();

        Assert.Equal("15/10/22/10", Stats(database, "hero"));
        // The migration shifts by 990 = 1000 - 10, so the distance above the origin is unchanged.
        Assert.Equal(1005 - 1000, 15 - 10);
        Assert.Equal(1012 - 1000, 22 - 10);
        // PointsSpent over the migrated row is the same 17 points. Written against the live constants,
        // which are now the origin the migration targets for good: StartStrength and StartVitality are
        // both NewCharacterAttribute = 10, so the total is asserted outright.
        int expected = Math.Max(0, 15 - PlayerStatRules.StartStrength) + Math.Max(0, 22 - PlayerStatRules.StartVitality);
        Assert.Equal(expected, PlayerStatRules.PointsSpent(15, PlayerStatRules.StartAttackSpeed, 22, 10, 10));
        Assert.Equal(17, expected);
    }

    [Fact]
    public void ALegacyCharacterBelowTheOldOrigin_IsUntouched_SoItsSpendsAreNotFlooredAway()
    {
        using MigrationTestDatabase database = V14With("('legacy', 'Legacy', 14, 10, 12, 10)");

        database.Schema.EnsureSchema();

        Assert.Equal("14/10/12/10", Stats(database, "legacy"));
    }

    [Fact]
    public void OnlyTheAttributeAtOrAbove1000_Moves()
    {
        using MigrationTestDatabase database = V14With("('hero', 'Hero', 1000, 12, 14, 11)");

        database.Schema.EnsureSchema();

        Assert.Equal("10/12/14/11", Stats(database, "hero"));
    }

    [Fact]
    public void A1000CharacterCreatedAfterTheCutoff_IsMigratedToTens()
    {
        using MigrationTestDatabase database = V14With("('hero', 'Hero', 1000, 1000, 1000, 1000)");
        database.ExecuteScript("UPDATE player_profiles SET created_utc = '2026-11-04T19:44:38Z' WHERE player_name = 'hero';");
        SqliteConnection.ClearAllPools();

        database.Schema.EnsureSchema();

        Assert.Equal("10/10/10/10", Stats(database, "hero"));
    }

    [Fact]
    public void A1000CharacterCreatedBeforeTheCutoff_IsUntouched_SoAClampedLegacyCharacterKeepsItsInvestment()
    {
        using MigrationTestDatabase database = V14With("('legacy', 'Legacy', 1000, 12, 1000, 10)");
        database.ExecuteScript("UPDATE player_profiles SET created_utc = '2026-11-04T19:44:36Z' WHERE player_name = 'legacy';");
        SqliteConnection.ClearAllPools();

        database.Schema.EnsureSchema();

        Assert.Equal("1000/12/1000/10", Stats(database, "legacy"));
    }

    [Fact]
    public void ACharacterCreatedExactlyAtTheCutoff_IsMigrated()
    {
        using MigrationTestDatabase database = V14With("('edge', 'Edge', 1000, 1000, 1005, 1000)");
        database.ExecuteScript("UPDATE player_profiles SET created_utc = '2026-11-04T19:44:37Z' WHERE player_name = 'edge';");
        SqliteConnection.ClearAllPools();

        database.Schema.EnsureSchema();

        Assert.Equal("10/10/15/10", Stats(database, "edge"));
    }

    [Fact]
    public void AStored2000CreatedAfterTheCutoff_Becomes1010_BecauseTheSpendIsPreserved()
    {
        using MigrationTestDatabase database = V14With("('hero', 'Hero', 2000, 1000, 1000, 1000)");

        database.Schema.EnsureSchema();

        Assert.Equal("1010/10/10/10", Stats(database, "hero"));
    }

    [Fact]
    public void StoredHealth_IsNotTouched_BecauseTheLoadClampsItToTheRecomputedMaximum()
    {
        using MigrationTestDatabase database = V14With("('hero', 'Hero', 1000, 1000, 1000, 1000)");
        database.ExecuteScript("UPDATE player_profiles SET current_hp = 5050, max_hp = 5050 WHERE player_name = 'hero';");
        SqliteConnection.ClearAllPools();

        database.Schema.EnsureSchema();

        // No backfill: max health is a formula in combat_formulas. The stale pair is harmless and
        // self-healing. PlayerCombatant.DeriveFromSheet recomputes DerivedStats from that expression
        // and then calls Health.SetMaxHealthPreservingRatio, so a stored 5050/5050 -- a ratio of
        // exactly 1.0 -- loads as 100/100 at vitality 10 (50 + 10 * 5); HealthState.SetAuthoritativeState
        // clamps any other stored ratio into [0, MaxHealth]. Nor is this pair the authority for current
        // health: that is player_world_state.current_hp, keyed by (world_id, player_name), and that
        // table has no max_hp column at all. A subsequent checkpoint rewrites both columns here
        // (CheckpointUpdateVitalsSql), retiring the stored 5050 for good.
        Assert.Equal(5050L, database.Scalar("SELECT current_hp FROM player_profiles WHERE player_name = 'hero';"));
        Assert.Equal(5050L, database.Scalar("SELECT max_hp FROM player_profiles WHERE player_name = 'hero';"));
    }

    [Fact]
    public void TheMigratorReachesTheShippedLatestVersion()
    {
        using MigrationTestDatabase database = V14With("('hero', 'Hero', 1000, 1000, 1000, 1000)");

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(MigrationTestDatabase.ShippedLatestVersion, result.ToVersion);
        Assert.Equal(15, result.ToVersion);
    }
}
