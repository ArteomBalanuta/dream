using System.Globalization;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// Persistence wave I (C0a): migration 0009 seeds the four enemy and armor formulas once. No table is
// rebuilt, so every enemy row and all five object-id triggers stay, and a formula row edited by hand
// is kept.
public sealed class EnemyFormulasMigrationTests
{
    private static readonly string[] Triggers =
    [
        "trg_cube_fracture_pieces_advance_object_id",
        "trg_enemy_instances_advance_object_id",
        "trg_item_instances_advance_object_id",
        "trg_placed_cube_instances_advance_object_id",
        "trg_terrain_cell_overrides_advance_object_id"
    ];

    private static readonly string[] NewKeys =
    [
        "enemy.attack_damage",
        "enemy.attack_interval_seconds",
        "enemy.max_health",
        "player.damage_taken_factor"
    ];

    public static TheoryData<int> FromVersions => new() { 0, 1, 2, 3, 4, 5, 6, 7, 8 };

    [Theory]
    [MemberData(nameof(FromVersions))]
    public void EveryEarlierVersion_MigratesToTheLatest_Clean(int fromVersion)
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

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(fromVersion, result.FromVersion);
        Assert.Equal(16, result.ToVersion);
        Assert.Equal(16, MigrationTestDatabase.ShippedLatestVersion);
        using SqliteConnection c = database.Open();
        Assert.Equal("ok", Scalar(c, "PRAGMA integrity_check;"));
        Assert.Empty(Rows(c, "PRAGMA foreign_key_check;"));
        Assert.Equal(Triggers, Rows(c, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name LIKE '%advance_object_id' ORDER BY name;"));
        Assert.Equal(NewKeys, Rows(c, "SELECT formula_key FROM combat_formulas WHERE formula_key IN ('enemy.attack_damage','enemy.max_health','enemy.attack_interval_seconds','player.damage_taken_factor') ORDER BY formula_key;"));
    }

    [Fact]
    public void AFreshDatabase_HasTwelveFormulas_ThatCompileInTheFormulaSet()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();

        using SqliteConnection c = database.Open();
        Assert.Equal(12L, Scalar(c, "SELECT COUNT(*) FROM combat_formulas;"));
        Assert.Equal(12L, Scalar(c, "SELECT COUNT(*) FROM combat_formulas WHERE enabled = 1;"));
        IReadOnlyList<CombatFormulaDefinition> definitions = new SqliteCombatFormulaRepository(database.Schema).LoadAllEnabled();
        CombatFormulaService service = CombatFormulaService.CreateForDefinitions(definitions);

        EnemyCombatDerivedStats wolf = service.EvaluateEnemyDerivedStats(strength: 10, attackSpeed: 0.952381f, vitality: 10, level: 1, intervalOverride: null);
        Assert.Equal(12, wolf.AttackDamage);
        Assert.Equal(100, wolf.MaxHealth);
        Assert.Equal(1.0, service.EvaluateDamageTakenFactor(0));
        Assert.Equal(100.0 / 140.0, service.EvaluateDamageTakenFactor(40), 12);
    }

    [Fact]
    public void TheShippedExpressions_EqualTheServiceDefaults()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();

        using SqliteConnection c = database.Open();
        Assert.Equal(
            [
                "enemy.attack_damage|strength + 2",
                "enemy.attack_interval_seconds|1.0 / attack_speed",
                "enemy.max_health|50 + vitality * 5",
                "player.damage_taken_factor|100 / (100 + armor)"
            ],
            Rows(c, "SELECT formula_key || '|' || expression_text FROM combat_formulas WHERE formula_key IN ('enemy.attack_damage','enemy.max_health','enemy.attack_interval_seconds','player.damage_taken_factor') ORDER BY formula_key;"));
    }

    [Fact]
    public void EveryEnemyRow_IsByteEqual_BeforeAndAfter()
    {
        using MigrationTestDatabase database = V8WithEnemies();
        string before = DumpEnemies(database);
        Assert.NotEmpty(before);
        string otherTablesBefore = DumpOtherTables(database);

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(8, result.FromVersion);
        Assert.Equal(16, result.ToVersion);
        Assert.Equal(before, DumpEnemies(database));
        Assert.Equal(otherTablesBefore, DumpOtherTables(database));
        using SqliteConnection c = database.Open();
        Assert.Equal(3L, Scalar(c, "SELECT COUNT(*) FROM enemy_instances;"));
    }

    [Fact]
    public void AHandEditedFormula_Stays_AndTheOtherThreeAreStillSeeded()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpTo(8));
        database.ExecuteScript(
            "INSERT INTO combat_formulas (formula_key, expression_text, description, enabled) " +
            "VALUES ('enemy.max_health', '10 + vitality * 9', 'Edited by hand.', 1);");

        database.Schema.EnsureSchema();

        using SqliteConnection c = database.Open();
        Assert.Equal("10 + vitality * 9", Scalar(c, "SELECT expression_text FROM combat_formulas WHERE formula_key = 'enemy.max_health';"));
        Assert.Equal("Edited by hand.", Scalar(c, "SELECT description FROM combat_formulas WHERE formula_key = 'enemy.max_health';"));
        Assert.Equal(12L, Scalar(c, "SELECT COUNT(*) FROM combat_formulas;"));
        Assert.Equal("enemy_level * 14", Scalar(c, "SELECT expression_text FROM combat_formulas WHERE formula_key = 'enemy.experience_reward';"));
    }

    [Fact]
    public void RunningTheScriptAgain_ChangesNothing()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        string before = string.Join(";", Rows(database.Open(), "SELECT * FROM combat_formulas ORDER BY formula_key;"));

        database.ExecuteScript(File.ReadAllText(Path.Combine(MigrationTestDatabase.MigrationsDirectory, "0009_enemy_formulas.sql")));

        Assert.Equal(before, string.Join(";", Rows(database.Open(), "SELECT * FROM combat_formulas ORDER BY formula_key;")));
    }

    [Fact]
    public void ABackupOfTheV8File_IsTaken_AndHasNoNewFormulas()
    {
        using MigrationTestDatabase database = V8WithEnemies();

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(16, result.ToVersion);
        string backup = Assert.Single(database.Backups(), path => path.Contains("v0008-to-v0016", StringComparison.Ordinal));
        Assert.Matches(@"persistence\.sqlite\.v0008-to-v0016\.\d{8}T\d{6}Z\.bak$", backup);
        using SqliteConnection old = MigrationTestDatabase.OpenFile(backup);
        Assert.Equal(0L, Scalar(old, "SELECT COUNT(*) FROM combat_formulas WHERE formula_key IN ('enemy.attack_damage','enemy.max_health','enemy.attack_interval_seconds','player.damage_taken_factor');"));
        Assert.Equal(3L, Scalar(old, "SELECT COUNT(*) FROM enemy_instances;"));
    }

    [Fact]
    public void AForcedFailure_RollsBackTo8_WithoutTheFormulas()
    {
        using MigrationTestDatabase database = V8WithEnemies();
        List<ISchemaMigration> migrations = [.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory)];
        migrations.Add(new FailingMigration(migrations.Count + 1));
        SqliteSchemaMigrator migrator = new(migrations);

        Assert.Throws<InvalidOperationException>(() => database.Schema.EnsureSchema(migrator));

        SqlitePools.Release(database.DatabasePath);
        using SqliteConnection c = database.Open();
        Assert.Equal(8, migrator.GetCurrentVersion(c));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM combat_formulas WHERE formula_key IN ('enemy.attack_damage','enemy.max_health','enemy.attack_interval_seconds','player.damage_taken_factor');"));
        Assert.Equal(3L, Scalar(c, "SELECT COUNT(*) FROM enemy_instances;"));
    }

    [Fact]
    public void ALatestFile_OnAV8Build_IsTooNew_AndTheServerExitCodeIsThree()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        SqliteSchemaMigrator v8Build = UpTo(8);

        using SqliteConnection c = database.Open();
        SchemaTooNewException error = Assert.Throws<SchemaTooNewException>(() => v8Build.Migrate(database.DatabasePath, c));

        Assert.Equal(16, error.StoredVersion);
        Assert.Equal(8, error.LatestVersion);
        Assert.Equal(3, ServerBootstrap.SchemaTooNewExitCode);
    }

    private static MigrationTestDatabase V8WithEnemies()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpTo(8));
        // Three wolves with different positions and a hand-set attack speed that must come back bit for bit.
        database.ExecuteScript(
            "INSERT INTO enemy_instances (world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, " +
            "facing_x, facing_y, facing_z, current_hp, max_hp, strength, agility, vitality, energy, movement_speed, attack_speed, level) VALUES " +
            "(1, 21, 'Wolf', 'Wolf', 5.5, 1, 2.5, 0, 0, 1, 100, 100, 10, 10, 10, 0, 2.8, 0.95238096, 1), " +
            "(1, 22, 'Wolf', 'Wolf', -3.25, 4, 9.125, 1, 0, 0, 37, 100, 10, 10, 10, 0, 2.8, 0.95238096, 1), " +
            "(1, 23, 'DireWolf', 'Dire Wolf', 1, 1, 1, 0, 0, -1, 0, 200, 16, 10, 30, 0, 3.2, 1, 3);");
        return database;
    }

    private static SqliteSchemaMigrator UpTo(int version) =>
        new([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(version)]);

    // Every enemy column, row by row, as quoted text (floats round-trip through quote()).
    private static string DumpEnemies(MigrationTestDatabase database)
    {
        using SqliteConnection c = database.Open();
        // camp_key (migration 0011) is new and NULL on every old row; the rest of the row must not change.
        List<string> columns = Rows(c, "SELECT name FROM pragma_table_info('enemy_instances') WHERE name <> 'camp_key' ORDER BY cid;");
        return string.Join(";", Rows(c, $"SELECT {string.Join(" || '|' || ", columns.Select(name => $"quote({name})"))} FROM enemy_instances ORDER BY 1;"));
    }

    private static string DumpOtherTables(MigrationTestDatabase database)
    {
        using SqliteConnection c = database.Open();
        string[] tables = ["worlds", "player_profiles", "player_world_state", "placed_cube_instances", "cube_fracture_pieces", "item_instances", "player_inventory_slots", "player_starter_grants"];
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

    private sealed class FailingMigration(int version) : ISchemaMigration
    {
        public int Version { get; } = version;

        public string Name => "fails";

        public void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE added_after_enemy_formulas (id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
            throw new InvalidOperationException("migration failed after a partial change");
        }
    }
}
