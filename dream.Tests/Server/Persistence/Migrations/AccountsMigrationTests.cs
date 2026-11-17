using System.Globalization;
using Dream.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// Persistence wave G (C0): migration 0007 adds accounts and player_profiles.account_id, and makes
// display_name unique ignoring ASCII case by renaming the later of any colliding rows to "<name>~<n>".
public sealed class AccountsMigrationTests
{
    private const int ConstraintError = 19; // SQLITE_CONSTRAINT

    private static readonly string[] Triggers =
    [
        "trg_cube_fracture_pieces_advance_object_id",
        "trg_enemy_instances_advance_object_id",
        "trg_item_instances_advance_object_id",
        "trg_placed_cube_instances_advance_object_id",
        "trg_terrain_cell_overrides_advance_object_id"
    ];

    public static TheoryData<int> FromVersions => new() { 0, 1, 2, 3, 4, 5, 6 };

    [Theory]
    [MemberData(nameof(FromVersions))]
    public void EveryEarlierVersion_MigratesToSeven_Clean(int fromVersion)
    {
        using MigrationTestDatabase database = new();
        if (fromVersion == 0)
        {
            database.BuildWithPreMigrationBootstrapper();
        }
        else
        {
            database.Schema.EnsureSchema(UpTo(fromVersion));
        }

        SchemaMigrationResult result = database.Schema.EnsureSchema(UpTo(7));

        Assert.Equal(fromVersion, result.FromVersion);
        Assert.Equal(7, result.ToVersion);
        using SqliteConnection c = database.Open();
        Assert.Equal("ok", Scalar(c, "PRAGMA integrity_check;"));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(Triggers, Rows(c, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name LIKE '%advance_object_id' ORDER BY name;"));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('player_profiles') WHERE name = 'account_id';"));
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ux_player_profiles_display_name';"));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%__v7';"));
    }

    [Fact]
    public void ThreeNamesDifferingByCase_KeepTheOldest_AndNumberTheOthers()
    {
        using MigrationTestDatabase database = V6();
        Profile(database, "k1", "Bob", "2026-01-01T00:00:01.000Z");
        Profile(database, "k2", "bob", "2026-01-01T00:00:02.000Z");
        Profile(database, "k3", "BOB", "2026-01-01T00:00:03.000Z");

        database.Schema.EnsureSchema();

        Assert.Equal(["k1=Bob", "k2=bob~2", "k3=BOB~3"], Names(database, "k%"));
    }

    [Fact]
    public void TheOldestKeeps_NotTheFirstInserted()
    {
        using MigrationTestDatabase database = V6();
        Profile(database, "k1", "Amy", "2026-01-01T00:00:05.000Z");
        Profile(database, "k2", "AMY", "2026-01-01T00:00:01.000Z");

        database.Schema.EnsureSchema();

        Assert.Equal(["k1=Amy~2", "k2=AMY"], Names(database, "k%"));
    }

    [Fact]
    public void TwoRowsWithTheSameDisplayNameExactly_AreSeparated()
    {
        using MigrationTestDatabase database = V6();
        Profile(database, "b-key", "Zed", "2026-01-01T00:00:01.000Z");
        Profile(database, "a-key", "Zed", "2026-01-01T00:00:01.000Z");

        database.Schema.EnsureSchema();

        // The same created_utc: player_name breaks the tie.
        Assert.Equal(["a-key=Zed", "b-key=Zed~2"], Names(database, "%-key"));
    }

    [Fact]
    public void LiteralTildeNames_AreNeverCollidedWith()
    {
        using MigrationTestDatabase database = V6();
        Profile(database, "k1", "bob", "2026-01-01T00:00:01.000Z");
        Profile(database, "k2", "bob~2", "2026-01-01T00:00:02.000Z");
        Profile(database, "k3", "BOB~2", "2026-01-01T00:00:03.000Z");
        Profile(database, "k4", "bob~3", "2026-01-01T00:00:04.000Z");
        Profile(database, "k5", "BOB", "2026-01-01T00:00:05.000Z");

        database.Schema.EnsureSchema();

        Assert.Equal(["k1=bob", "k2=bob~2", "k3=BOB~2~2", "k4=bob~3", "k5=BOB~4"], Names(database, "k%"));
        using SqliteConnection c = database.Open();
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM (SELECT 1 FROM player_profiles GROUP BY display_name COLLATE NOCASE HAVING COUNT(*) > 1);"));
    }

    [Fact]
    public void NonAsciiCaseDifferences_AreDistinctNames_AndStay()
    {
        using MigrationTestDatabase database = V6();
        Profile(database, "k1", "Äb", "2026-01-01T00:00:01.000Z");
        Profile(database, "k2", "äb", "2026-01-01T00:00:02.000Z");

        database.Schema.EnsureSchema();

        Assert.Equal(["k1=Äb", "k2=äb"], Names(database, "k%"));
    }

    [Fact]
    public void Rows_WorldRows_AndInventory_AreUntouched_ApartFromTheThreeColumns()
    {
        using MigrationTestDatabase database = V6WithData();
        Profile(database, "alice2", "ALICE", "2099-01-01T00:00:00.000Z");
        database.ExecuteScript(
            "INSERT INTO player_world_state (world_id, player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z) " +
            "VALUES (1, 'alice', 1, 2, 3, 0, 0, 1), (1, 'alice2', 4, 5, 6, 0, 0, 1);");
        string before = DumpExcept(database, "player_profiles", "display_name", "updated_utc");
        string other = DumpTables(database);
        string profilesBefore = DumpExcept(database, "player_profiles");

        database.Schema.EnsureSchema(UpTo(7));

        Assert.Equal(before, DumpExcept(database, "player_profiles", "display_name", "updated_utc"));
        Assert.Equal(other, DumpTables(database));
        Assert.NotEqual(profilesBefore, DumpExcept(database, "player_profiles"));
        using SqliteConnection c = database.Open();
        Assert.Equal(["alice2=ALICE~2", "alice=Alice", "bob=Bob"], Names(database, "%").Select(n => n).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM player_profiles WHERE account_id IS NOT NULL;"));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public void TwoCopiesOfOneDatabase_MigrateIdentically()
    {
        using MigrationTestDatabase a = V6();
        using MigrationTestDatabase b = V6();
        foreach (MigrationTestDatabase database in new[] { a, b })
        {
            Profile(database, "k1", "Bob", "2026-01-01T00:00:01.000Z");
            Profile(database, "k2", "bob", "2026-01-01T00:00:01.000Z");
            Profile(database, "k3", "BOB", "2026-01-01T00:00:01.000Z");
            Profile(database, "k4", "bob~2", "2026-01-01T00:00:02.000Z");
        }

        a.Schema.EnsureSchema();
        b.Schema.EnsureSchema();

        Assert.Equal(Names(a, "%"), Names(b, "%"));
        Assert.Equal(DumpExcept(a, "player_profiles", "updated_utc"), DumpExcept(b, "player_profiles", "updated_utc"));
    }

    [Fact]
    public void ABackupHoldsTheOldNames()
    {
        using MigrationTestDatabase database = V6();
        Profile(database, "k1", "Bob", "2026-01-01T00:00:01.000Z");
        Profile(database, "k2", "BOB", "2026-01-01T00:00:02.000Z");

        SchemaMigrationResult result = database.Schema.EnsureSchema(UpTo(7));

        Assert.Equal(7, result.ToVersion);
        string backup = Assert.Single(database.Backups());
        Assert.Matches(@"persistence\.sqlite\.v0006-to-v0007\.\d{8}T\d{6}Z\.bak$", backup);
        using SqliteConnection old = MigrationTestDatabase.OpenFile(backup);
        Assert.Equal(["k1=Bob", "k2=BOB"], Rows(old, "SELECT player_name || '=' || display_name FROM player_profiles WHERE player_name LIKE 'k%' ORDER BY player_name;"));
        Assert.Equal(0L, Scalar(old, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'accounts';"));
    }

    [Fact]
    public void AForcedFailure_RollsBackTo6_WithTheOldNames()
    {
        using MigrationTestDatabase database = V6();
        Profile(database, "k1", "Bob", "2026-01-01T00:00:01.000Z");
        Profile(database, "k2", "BOB", "2026-01-01T00:00:02.000Z");
        List<ISchemaMigration> migrations = [.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory)];
        migrations.Add(new FailingMigration(migrations.Count + 1));
        SqliteSchemaMigrator migrator = new(migrations);

        Assert.Throws<InvalidOperationException>(() => database.Schema.EnsureSchema(migrator));

        SqlitePools.Release(database.DatabasePath);
        using SqliteConnection c = database.Open();
        Assert.Equal(6, migrator.GetCurrentVersion(c));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('accounts', 'ux_player_profiles_display_name');"));
        Assert.Equal(["k1=Bob", "k2=BOB"], Names(database, "k%"));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('player_profiles') WHERE name = 'account_id';"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void ATokenHashThatIsNot32Bytes_IsRefused(int length)
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();

        SqliteException error = Assert.Throws<SqliteException>(() => MigrationTestDatabase.Execute(
            c,
            $"INSERT INTO accounts (token_hash, created_utc, last_seen_utc) VALUES (zeroblob({length}), 'x', 'x');"));

        Assert.Equal(ConstraintError, error.SqliteErrorCode);
        MigrationTestDatabase.Execute(c, "INSERT INTO accounts (token_hash, created_utc, last_seen_utc) VALUES (zeroblob(32), 'x', 'x');");
        Assert.Throws<SqliteException>(() => MigrationTestDatabase.Execute(
            c,
            "INSERT INTO accounts (token_hash, created_utc, last_seen_utc) VALUES (zeroblob(32), 'y', 'y');"));
    }

    [Fact]
    public void TheUniqueIndex_RefusesANocaseDuplicate_ButNotANonAsciiOne()
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();
        MigrationTestDatabase.Execute(c, "INSERT INTO player_profiles (player_name, display_name) VALUES ('x1', 'Amy'), ('x3', 'Äb');");

        SqliteException error = Assert.Throws<SqliteException>(() => MigrationTestDatabase.Execute(
            c,
            "INSERT INTO player_profiles (player_name, display_name) VALUES ('x2', 'aMY');"));

        Assert.Equal(ConstraintError, error.SqliteErrorCode);
        MigrationTestDatabase.Execute(c, "INSERT INTO player_profiles (player_name, display_name) VALUES ('x4', 'äb');");
    }

    [Fact]
    public void DeletingAnAccount_ReleasesItsCharacters_WhenForeignKeysAreOn()
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();
        MigrationTestDatabase.Execute(c, "PRAGMA foreign_keys = ON;");
        MigrationTestDatabase.Execute(c, "INSERT INTO accounts (token_hash, created_utc, last_seen_utc) VALUES (zeroblob(32), 'x', 'x');");
        MigrationTestDatabase.Execute(c, "INSERT INTO player_profiles (player_name, display_name, account_id) VALUES ('x1', 'Amy', 1);");
        Assert.Throws<SqliteException>(() => MigrationTestDatabase.Execute(c, "INSERT INTO player_profiles (player_name, display_name, account_id) VALUES ('x2', 'Ben', 99);"));

        MigrationTestDatabase.Execute(c, "DELETE FROM accounts WHERE account_id = 1;");

        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM player_profiles WHERE player_name = 'x1' AND account_id IS NULL;"));
    }

    private static MigrationTestDatabase Latest()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        return database;
    }

    private static MigrationTestDatabase V6()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpTo(6));
        return database;
    }

    private static MigrationTestDatabase V6WithData()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpTo(4));
        database.ExecuteScript(File.ReadAllText(Path.Combine(
            Dream.Tests.Integration.RepoRoot.Find(), "dream.Tests", "Server", "Persistence", "Migrations", "Fixtures",
            "v4-with-data.sql")));
        database.Schema.EnsureSchema(UpTo(6));
        return database;
    }

    private static void Profile(MigrationTestDatabase database, string key, string displayName, string createdUtc) =>
        database.ExecuteScript(
            $"INSERT INTO player_profiles (player_name, display_name, created_utc, updated_utc) VALUES ('{key}', '{displayName}', '{createdUtc}', '{createdUtc}');");

    private static SqliteSchemaMigrator UpTo(int version) =>
        new([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(version)]);

    private static List<string> Names(MigrationTestDatabase database, string keyLike)
    {
        using SqliteConnection c = database.Open();
        return Rows(c, $"SELECT player_name || '=' || display_name FROM player_profiles WHERE player_name LIKE '{keyLike}' ORDER BY player_name;");
    }

    // Every column of a table except the named ones, row by row.
    private static string DumpExcept(MigrationTestDatabase database, string table, params string[] skip)
    {
        using SqliteConnection c = database.Open();
        List<string> columns = Rows(c, $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;")
            .Where(name => !skip.Contains(name) && name != "account_id")
            .ToList();
        return string.Join(";", Rows(c, $"SELECT {string.Join(" || '|' || ", columns.Select(name => $"quote({name})"))} FROM {table} ORDER BY 1;"));
    }

    private static string DumpTables(MigrationTestDatabase database)
    {
        using SqliteConnection c = database.Open();
        string[] tables = ["worlds", "player_world_state", "placed_cube_instances", "cube_fracture_pieces", "enemy_instances", "item_instances", "player_inventory_slots"];
        return string.Join("\n", tables.Select(t => t + ":" + string.Join(";", Rows(c, $"SELECT * FROM {t} ORDER BY 1, 2, 3;"))));
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static List<string> Rows(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            string[] cells = new string[reader.FieldCount];
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
            }

            rows.Add(string.Join("|", cells));
        }

        return rows;
    }

    // Writes something, then fails: the migrator's transaction must undo every pending step.
    private sealed class FailingMigration(int version) : ISchemaMigration
    {
        public int Version { get; } = version;

        public string Name => "fails";

        public void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE added_after_accounts (id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
            throw new InvalidOperationException("migration failed after a partial change");
        }
    }
}
