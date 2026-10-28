using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// Persistence wave A (A1): the migration runner on new databases, re-runs, failures and the
// migration directory's validation. Legacy databases are covered by LegacySchemaMigrationTests.
public sealed class SchemaMigrationTests
{
    [Fact]
    public void ShippedMigrations_LoadAsTheBaselineOnly()
    {
        IReadOnlyList<ISchemaMigration> migrations = SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory);

        ISchemaMigration baseline = Assert.Single(migrations);
        Assert.IsType<BaselineMigration>(baseline);
        Assert.Equal(1, baseline.Version);
        Assert.Equal("baseline", baseline.Name);
    }

    [Fact]
    public void BaselineScript_IsTheInitScriptByteForByte_PlusWorldMetadata()
    {
        string init = File.ReadAllText(MigrationTestDatabase.InitScriptPath).ReplaceLineEndings("\n");
        string baseline = File.ReadAllText(Path.Combine(MigrationTestDatabase.MigrationsDirectory, "0001_baseline.sql"))
            .ReplaceLineEndings("\n");
        const string firstStatement = "PRAGMA foreign_keys = ON;";
        string initStatements = init[init.IndexOf(firstStatement, StringComparison.Ordinal)..];
        string baselineStatements = baseline[baseline.IndexOf(firstStatement, StringComparison.Ordinal)..];

        Assert.Equal(
            initStatements + "\nCREATE TABLE IF NOT EXISTS world_metadata (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);\n",
            baselineStatements);
    }

    [Fact]
    public void NewDatabase_GetsTodaysSchema_ColumnOrderIncluded_AtVersion1()
    {
        using MigrationTestDatabase reference = new();
        reference.BuildWithPreMigrationBootstrapper();
        using MigrationTestDatabase database = new();
        List<string> log = [];

        SchemaMigrationResult result = database.Schema.EnsureSchema(log.Add);

        Assert.Equal(new SchemaMigrationResult(0, 1, null), result);
        Assert.Empty(database.Backups());
        Assert.Contains(log, line => line.Contains("version 1", StringComparison.Ordinal));
        using SqliteConnection expected = reference.Open();
        using SqliteConnection actual = database.Open();
        Assert.Equal(SqliteSchemaSnapshot.Capture(expected), SqliteSchemaSnapshot.Capture(actual));
        // The seed rows too (the formula seed is part of the baseline); updated_utc is the insert time.
        const string seed = "SELECT formula_key || '|' || expression_text || '|' || description || '|' || enabled FROM combat_formulas ORDER BY formula_key;";
        Assert.Equal(Column(expected, seed), Column(actual, seed));
        Assert.Equal(8, Column(actual, seed).Count);
        Assert.Equal([(1L, "baseline")], VersionRows(actual));
        string appliedUtc = Convert.ToString(database.Scalar("SELECT applied_utc FROM schema_version;"))!;
        Assert.Equal(SqliteSchemaBootstrapper.FormatUtc(SqliteSchemaBootstrapper.ParseUtc(appliedUtc)), appliedUtc);
    }

    [Fact]
    public void SecondEnsureSchema_IsANoOp()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        database.ExecuteScript("INSERT INTO world_metadata (key, value) VALUES ('kept', 'yes');");
        database.ExecuteScript("DELETE FROM combat_formulas WHERE formula_key = 'player.critical_chance';");
        List<string> log = [];

        SchemaMigrationResult result = database.Schema.EnsureSchema(log.Add);

        Assert.Equal(new SchemaMigrationResult(1, 1, null), result);
        Assert.Empty(log);
        Assert.Empty(database.Backups());
        using SqliteConnection connection = database.Open();
        Assert.Equal([(1L, "baseline")], VersionRows(connection));
        Assert.Equal("yes", database.Scalar("SELECT value FROM world_metadata WHERE key = 'kept';"));
        // The accepted behaviour change: the baseline runs once, so a deleted seed row stays deleted.
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM combat_formulas WHERE formula_key = 'player.critical_chance';"));
    }

    [Fact]
    public void StoredVersionNewerThanTheBuild_Throws_AndLeavesTheFileUntouched()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        database.ExecuteScript("INSERT INTO schema_version (version, name, applied_utc) VALUES (99, 'from_the_future', '2030-01-01T00:00:00.0000000Z');");
        // Rollback journal, so a WAL switch would show in the file header. Leaving WAL needs the only
        // connection to the file, so the bootstrapper's pooled ones are closed first.
        MigrationTestDatabase.ReleasePooledConnections();
        database.ExecuteScript("PRAGMA journal_mode=DELETE;");
        MigrationTestDatabase.ReleasePooledConnections();
        byte[] before = File.ReadAllBytes(database.DatabasePath);

        SchemaTooNewException error = Assert.Throws<SchemaTooNewException>(() => database.Schema.EnsureSchema());

        MigrationTestDatabase.ReleasePooledConnections();
        Assert.Equal(99, error.StoredVersion);
        Assert.Equal(1, error.LatestVersion);
        Assert.Equal(before, File.ReadAllBytes(database.DatabasePath));
        Assert.Empty(database.Backups());
        Assert.False(File.Exists(database.DatabasePath + "-wal"));
    }

    [Fact]
    public void FailingMigration_RollsBackEveryPendingStep_KeepsVersion1_AndLeavesTheBackup()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        database.ExecuteScript("INSERT INTO world_metadata (key, value) VALUES ('before', 'v1');");
        List<ISchemaMigration> migrations = [.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory)];
        migrations.Add(new SqlScriptMigration(2, "adds_a_table", "CREATE TABLE added_by_two (id INTEGER PRIMARY KEY);"));
        migrations.Add(new FailingMigration(3));
        SqliteSchemaMigrator migrator = new(migrations);

        Assert.Throws<InvalidOperationException>(() => database.Schema.EnsureSchema(migrator));

        MigrationTestDatabase.ReleasePooledConnections();
        using SqliteConnection connection = database.Open();
        Assert.Equal(1, migrator.GetCurrentVersion(connection));
        Assert.Equal([(1L, "baseline")], VersionRows(connection));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('added_by_two', 'added_by_three');"));
        Assert.Equal("v1", database.Scalar("SELECT value FROM world_metadata WHERE key = 'before';"));
        string backup = Assert.Single(database.Backups());
        Assert.Matches(@"persistence\.sqlite\.v0001-to-v0003\.\d{8}T\d{6}Z\.bak$", backup);
        using SqliteConnection backupConnection = MigrationTestDatabase.OpenFile(backup);
        Assert.Equal(SqliteSchemaSnapshot.DumpRows(connection), SqliteSchemaSnapshot.DumpRows(backupConnection));
    }

    [Fact]
    public void PendingMigrationsOnAVersionedDatabase_ApplyInOrder_InOneStep()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        List<ISchemaMigration> migrations = [.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory)];
        migrations.Add(new SqlScriptMigration(2, "add_a", "CREATE TABLE a (id INTEGER PRIMARY KEY);"));
        migrations.Add(new SqlScriptMigration(3, "add_b", "CREATE TABLE b (a_id INTEGER NOT NULL REFERENCES a(id));"));

        SchemaMigrationResult result = database.Schema.EnsureSchema(new SqliteSchemaMigrator(migrations));

        Assert.Equal(1, result.FromVersion);
        Assert.Equal(3, result.ToVersion);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        using SqliteConnection connection = database.Open();
        Assert.Equal([(1L, "baseline"), (2L, "add_a"), (3L, "add_b")], VersionRows(connection));
    }

    [Fact]
    public void NewDatabase_IsInWalMode()
    {
        using MigrationTestDatabase database = new();

        database.Schema.EnsureSchema();

        Assert.Equal("wal", database.Scalar("PRAGMA journal_mode;"));
    }

    [Theory]
    [InlineData(new[] { "0001_baseline.sql", "0003_skips_two.sql" }, "contiguous")]
    [InlineData(new[] { "0002_no_baseline.sql" }, "contiguous")]
    [InlineData(new[] { "0001_baseline.sql", "0002_first.sql", "0002_second.sql" }, "twice")]
    [InlineData(new[] { "0001_baseline.sql", "2_short.sql" }, "NNNN_snake_case")]
    [InlineData(new[] { "0001_baseline.sql", "0002_Upper_Case.sql" }, "NNNN_snake_case")]
    [InlineData(new string[0], "No schema migrations")]
    public void InvalidMigrationDirectory_Throws(string[] fileNames, string expectedMessage)
    {
        using MigrationTestDatabase database = new();
        string directory = Path.Combine(database.RootDirectory, "migrations");
        Directory.CreateDirectory(directory);
        foreach (string fileName in fileNames)
        {
            File.WriteAllText(Path.Combine(directory, fileName), "CREATE TABLE IF NOT EXISTS t (id INTEGER);");
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => SchemaMigrations.LoadFromDirectory(directory));

        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BEGIN;\nCREATE TABLE t (id INTEGER);")]
    [InlineData("BEGIN IMMEDIATE TRANSACTION;")]
    [InlineData("CREATE TABLE t (id INTEGER);\nCOMMIT;")]
    [InlineData("  rollback;")]
    [InlineData("PRAGMA journal_mode=DELETE;")]
    public void MigrationScript_ThatControlsTheTransactionOrJournal_Throws(string script)
    {
        using MigrationTestDatabase database = new();
        string directory = Path.Combine(database.RootDirectory, "migrations");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(MigrationTestDatabase.MigrationsDirectory, "0001_baseline.sql"), Path.Combine(directory, "0001_baseline.sql"));
        File.WriteAllText(Path.Combine(directory, "0002_bad.sql"), script);

        Assert.Throws<InvalidDataException>(() => SchemaMigrations.LoadFromDirectory(directory));
    }

    [Fact]
    public void MissingMigrationDirectory_Throws()
    {
        using MigrationTestDatabase database = new();

        Assert.Throws<DirectoryNotFoundException>(() =>
            SchemaMigrations.LoadFromDirectory(Path.Combine(database.RootDirectory, "missing")));
    }

    [Fact]
    public void SchemaScriptWithoutAMigrationsDirectory_IsItsOwnBaseline()
    {
        using MigrationTestDatabase database = new();
        string scriptPath = Path.Combine(database.RootDirectory, "schema.sql");
        File.WriteAllText(scriptPath, """
            CREATE TABLE IF NOT EXISTS player_profiles (player_name TEXT NOT NULL PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS placed_cube_instances (cube_id INTEGER NOT NULL PRIMARY KEY);
            """);
        SqliteSchemaBootstrapper schema = new(database.DatabasePath, scriptPath);

        SchemaMigrationResult result = schema.EnsureSchema();

        Assert.Equal(new SchemaMigrationResult(0, 1, null), result);
        using SqliteConnection connection = database.Open();
        Assert.Equal([(1L, "baseline")], VersionRows(connection));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'world_metadata';"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('player_profiles') WHERE name = 'experience';"));
        Assert.Equal(3L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('placed_cube_instances') WHERE name LIKE 'center_%';"));
    }

    [Fact]
    public void Migrator_RejectsAnInjectedGap()
    {
        ISchemaMigration[] migrations =
        [
            new SqlScriptMigration(1, "one", "SELECT 1;"),
            new SqlScriptMigration(3, "three", "SELECT 1;")
        ];

        Assert.Throws<InvalidDataException>(() => new SqliteSchemaMigrator(migrations));
    }

    [Fact]
    public void MigrationsDirectory_IsShippedToTheTestOutput_NextToTheInitScript()
    {
        string outputConfig = Path.Combine(AppContext.BaseDirectory, "config");

        Assert.True(File.Exists(Path.Combine(outputConfig, "player-profiles.init.sql")));
        Assert.True(File.Exists(Path.Combine(outputConfig, "migrations", "0001_baseline.sql")));
        SqliteSchemaBootstrapper schema = new(
            Path.Combine(Path.GetTempPath(), "never-opened.sqlite"),
            Path.Combine(outputConfig, "player-profiles.init.sql"));
        Assert.Equal(Path.Combine(outputConfig, "migrations"), schema.MigrationsDirectory);
    }

    internal static List<(long Version, string Name)> VersionRows(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT version, name FROM schema_version ORDER BY version;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<(long, string)> rows = [];
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return rows;
    }

    private static List<string> Column(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> values = [];
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    // Writes something, then fails: the migrator's transaction must undo the write.
    private sealed class FailingMigration(int version) : ISchemaMigration
    {
        public int Version { get; } = version;

        public string Name => "fails";

        public void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE added_by_three (id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
            throw new InvalidOperationException("migration failed after a partial change");
        }
    }
}
