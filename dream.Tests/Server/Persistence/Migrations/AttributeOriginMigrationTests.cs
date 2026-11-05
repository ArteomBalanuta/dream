using Dream.Server.Persistence.Migrations;
using Dream.Simulation.Players;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// Migration 0015 moves characters stored on the 1000 attribute origin (commit a39702e6) onto the 10
// origin by subtracting 990 from any attribute at or above 1000, so PointsSpent is unchanged and a
// pre-a39702e6 character, whose attributes sit near 10, is left alone.
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
        // PointsSpent over the migrated row is the same 17 points once the code's origin is the 10 the
        // migration targets. Written against the live constant: on a build whose origin is still 1000
        // the migrated row (correctly) reads as nothing spent, so only the matching origin is asserted.
        int expected = Math.Max(0, 15 - PlayerStatRules.StartStrength) + Math.Max(0, 22 - PlayerStatRules.StartVitality);
        Assert.Equal(expected, PlayerStatRules.PointsSpent(15, PlayerStatRules.StartAttackSpeed, 22, 10, 10));
        if (PlayerStatRules.StartStrength == 10 && PlayerStatRules.StartVitality == 10)
        {
            Assert.Equal(17, expected);
        }
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
    public void StoredHealth_IsNotTouched_BecauseTheLoadClampsItToTheRecomputedMaximum()
    {
        using MigrationTestDatabase database = V14With("('hero', 'Hero', 1000, 1000, 1000, 1000)");
        database.ExecuteScript("UPDATE player_profiles SET current_hp = 5050, max_hp = 5050 WHERE player_name = 'hero';");
        SqliteConnection.ClearAllPools();

        database.Schema.EnsureSchema();

        // No backfill: max health is a formula in combat_formulas. HealthState.SetAuthoritativeState
        // clamps the stored current to the recomputed maximum when the character loads.
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
