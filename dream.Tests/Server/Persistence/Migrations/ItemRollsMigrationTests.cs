using System.Globalization;
using Dream.Server;
using Dream.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// Persistence wave H (C0): migration 0008 adds item_instances.rolled_modifiers, a nullable, size- and
// JSON-checked column. No table is rebuilt, so every row and all five object-id triggers stay.
public sealed class ItemRollsMigrationTests
{
    private const int ConstraintError = 19; // SQLITE_CONSTRAINT

    private const string ValidRoll = """{"v":1,"r":"Rare","m":[["Strength","Flat",6],["AttackSpeed","Flat",1.75]]}""";

    private static readonly string[] Triggers =
    [
        "trg_cube_fracture_pieces_advance_object_id",
        "trg_enemy_instances_advance_object_id",
        "trg_item_instances_advance_object_id",
        "trg_placed_cube_instances_advance_object_id",
        "trg_terrain_cell_overrides_advance_object_id"
    ];

    public static TheoryData<int> FromVersions => new() { 0, 1, 2, 3, 4, 5, 6, 7 };

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
        Assert.Equal(["rolled_modifiers|TEXT|0"], Rows(c, "SELECT name || '|' || type || '|' || \"notnull\" FROM pragma_table_info('item_instances') WHERE name = 'rolled_modifiers';"));
    }

    [Fact]
    public void TheNewColumn_IsTheLastOne_AndExistingRowsAreByteEqualApartFromIt()
    {
        using MigrationTestDatabase database = V7WithData();
        // visual_id is left out, the way DumpOtherTables leaves out player_inventory_slots: migration
        // 0016 realigns it to the content catalog on purpose, so it is no longer byte-stable across the
        // chain. This test is about 0008 appending rolled_modifiers without disturbing the rows, which
        // every other column still proves. 0016's own behaviour is asserted below and in
        // ItemVisualIdsMigrationTests.
        string before = DumpItems(database, includeRolls: false, includeVisual: false);
        Assert.NotEmpty(before);
        string otherTablesBefore = DumpOtherTables(database);

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(7, result.FromVersion);
        Assert.Equal(16, result.ToVersion);
        Assert.Equal(before, DumpItems(database, includeRolls: false, includeVisual: false));
        Assert.Equal(otherTablesBefore, DumpOtherTables(database));
        using SqliteConnection c = database.Open();
        // What 0016 did to this fixture's rows, which happen to cover both of its branches: tool.torch
        // is in the shipped catalog, so its seeded 'torch' is realigned; tool.pick is not in the
        // catalog at all, so its 'pick' is left exactly as stored rather than guessed at.
        Assert.Equal(
            "held.tool.torch",
            Scalar(c, "SELECT visual_id FROM item_instances WHERE item_kind = 'tool.torch';"));
        Assert.Equal(
            "pick",
            Scalar(c, "SELECT DISTINCT visual_id FROM item_instances WHERE item_kind = 'tool.pick';"));
        Assert.Equal(3L, Scalar(c, "SELECT COUNT(*) FROM item_instances;"));
        Assert.Equal(3L, Scalar(c, "SELECT COUNT(*) FROM item_instances WHERE rolled_modifiers IS NULL;"));
        string lastColumn = (string)Scalar(c, "SELECT name FROM pragma_table_info('item_instances') ORDER BY cid DESC LIMIT 1;")!;
        Assert.Equal("rolled_modifiers", lastColumn);
    }

    [Fact]
    public void TwoCopies_MigrateIdentically()
    {
        using MigrationTestDatabase a = V7WithData();
        using MigrationTestDatabase b = V7WithData();
        a.Schema.EnsureSchema();
        b.Schema.EnsureSchema();

        // Timestamps default to the insert time, so they differ between two builds of the fixture.
        Assert.Equal(DumpItems(a, includeRolls: true, includeTimes: false), DumpItems(b, includeRolls: true, includeTimes: false));
        Assert.Equal(DumpItems(a, includeRolls: true), DumpItems(a, includeRolls: true));
    }

    [Fact]
    public void ABackupOfTheV7File_IsTaken_AndHasNoNewColumn()
    {
        using MigrationTestDatabase database = V7WithData();

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(16, result.ToVersion);
        string backup = Assert.Single(database.Backups(), path => path.Contains("v0007-to-v0016", StringComparison.Ordinal));
        Assert.Matches(@"persistence\.sqlite\.v0007-to-v0016\.\d{8}T\d{6}Z\.bak$", backup);
        using SqliteConnection old = MigrationTestDatabase.OpenFile(backup);
        Assert.Equal(0L, Scalar(old, "SELECT COUNT(*) FROM pragma_table_info('item_instances') WHERE name = 'rolled_modifiers';"));
        Assert.Equal(3L, Scalar(old, "SELECT COUNT(*) FROM item_instances;"));
    }

    [Fact]
    public void AForcedFailure_RollsBackTo7_WithoutTheColumn()
    {
        using MigrationTestDatabase database = V7WithData();
        List<ISchemaMigration> migrations = [.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory)];
        migrations.Add(new FailingMigration(migrations.Count + 1));
        SqliteSchemaMigrator migrator = new(migrations);

        Assert.Throws<InvalidOperationException>(() => database.Schema.EnsureSchema(migrator));

        MigrationTestDatabase.ReleasePooledConnections();
        using SqliteConnection c = database.Open();
        Assert.Equal(7, migrator.GetCurrentVersion(c));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('item_instances') WHERE name = 'rolled_modifiers';"));
        Assert.Equal(3L, Scalar(c, "SELECT COUNT(*) FROM item_instances;"));
    }

    [Fact]
    public void AValidRoll_IsStored_AndBytesComeBackEqual()
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();

        Insert(c, 900, ValidRoll);
        Insert(c, 901, null);

        Assert.Equal(ValidRoll, Scalar(c, "SELECT rolled_modifiers FROM item_instances WHERE item_instance_id = 900;"));
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM item_instances WHERE item_instance_id = 901 AND rolled_modifiers IS NULL;"));
    }

    [Fact]
    public void AnInsertThatRaisesTheCounter_StillDoes_ForARolledItem()
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();
        long before = (long)Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 1;")!;

        Insert(c, before + 40, ValidRoll);

        Assert.Equal(before + 41, (long)Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 1;")!);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("{\"v\":1,}")]
    public void MalformedText_IsRefused(string text)
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();

        SqliteException error = Assert.Throws<SqliteException>(() => Insert(c, 900, text));

        Assert.Equal(ConstraintError, error.SqliteErrorCode);
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM item_instances;"));
    }

    [Fact]
    public void ALongValue_IsRefused_AtOneTooManyCharacters()
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();
        const string prefix = "{\"v\":1,\"r\":\"Rare\",\"m\":[],\"p\":\"";
        const string suffix = "\"}";
        string atLimit = prefix + new string('x', 1024 - prefix.Length - suffix.Length) + suffix;
        string tooLong = prefix + new string('x', 1025 - prefix.Length - suffix.Length) + suffix;
        Assert.Equal(1024, atLimit.Length);
        Assert.Equal(1025, tooLong.Length);

        Insert(c, 900, atLimit);
        SqliteException error = Assert.Throws<SqliteException>(() => Insert(c, 901, tooLong));
        Assert.Equal(ConstraintError, error.SqliteErrorCode);
        Assert.Throws<SqliteException>(() => Insert(c, 902, "{\"v\":1,\"p\":\"" + new string('x', 2000) + "\"}"));
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM item_instances;"));
    }

    [Fact]
    public void ADeeplyNestedArray_IsRefused()
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();

        Assert.Throws<SqliteException>(() => Insert(c, 900, new string('[', 1500)));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM item_instances;"));
    }

    [Fact]
    public void AnUpsertThatDoesNotNameTheColumn_KeepsTheRoll()
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();
        Insert(c, 900, ValidRoll);

        MigrationTestDatabase.Execute(
            c,
            "INSERT INTO item_instances (world_id, item_instance_id, item_kind, display_name, visual_id, state, rolled_modifiers) " +
            "VALUES (1, 900, 'other', 'Other', 'v', 'Inventory', '{\"v\":1,\"r\":\"Common\",\"m\":[]}') " +
            "ON CONFLICT (world_id, item_instance_id) DO UPDATE SET item_kind = excluded.item_kind;");

        Assert.Equal(ValidRoll, Scalar(c, "SELECT rolled_modifiers FROM item_instances WHERE item_instance_id = 900;"));
        Assert.Equal("other", Scalar(c, "SELECT item_kind FROM item_instances WHERE item_instance_id = 900;"));
    }

    [Fact]
    public void ALatestFile_OnAV7Build_IsTooNew_AndTheServerExitCodeIsThree()
    {
        using MigrationTestDatabase database = Latest();
        SqliteSchemaMigrator v7Build = UpTo(7);

        using SqliteConnection c = database.Open();
        SchemaTooNewException error = Assert.Throws<SchemaTooNewException>(() => v7Build.Migrate(database.DatabasePath, c));

        Assert.Equal(16, error.StoredVersion);
        Assert.Equal(7, error.LatestVersion);
        Assert.Equal(3, ServerBootstrap.SchemaTooNewExitCode);
    }

    private static void Insert(SqliteConnection c, long id, string? roll)
    {
        using SqliteCommand command = c.CreateCommand();
        command.CommandText =
            "INSERT INTO item_instances (world_id, item_instance_id, item_kind, display_name, visual_id, state, rolled_modifiers) " +
            "VALUES (1, $id, 'gear.iron-blade', 'Iron Blade', 'held.tool.blade', 'Inventory', $roll);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$roll", (object?)roll ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static MigrationTestDatabase Latest()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        return database;
    }

    private static MigrationTestDatabase V7WithData()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpTo(4));
        database.ExecuteScript(File.ReadAllText(Path.Combine(
            Dream.Tests.Integration.RepoRoot.Find(), "dream.Tests", "Server", "Persistence", "Migrations", "Fixtures",
            "v4-with-data.sql")));
        database.Schema.EnsureSchema(UpTo(7));
        return database;
    }

    private static SqliteSchemaMigrator UpTo(int version) =>
        new([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(version)]);

    // Every item column that exists before 0008, row by row, as quoted text.
    private static string DumpItems(MigrationTestDatabase database, bool includeRolls, bool includeTimes = true, bool includeVisual = true)
    {
        using SqliteConnection c = database.Open();
        List<string> columns = Rows(c, "SELECT name FROM pragma_table_info('item_instances') ORDER BY cid;")
            .Where(name => (includeRolls || name != "rolled_modifiers")
                && (includeTimes || !name.EndsWith("_utc", StringComparison.Ordinal))
                && (includeVisual || name != "visual_id"))
            .ToList();
        return string.Join(";", Rows(c, $"SELECT {string.Join(" || '|' || ", columns.Select(name => $"quote({name})"))} FROM item_instances ORDER BY 1;"));
    }

    private static string DumpOtherTables(MigrationTestDatabase database)
    {
        using SqliteConnection c = database.Open();
        // player_inventory_slots is left out: migration 0010 remaps its slot_index (see BagGridMigrationTests).
        string[] tables = ["worlds", "player_profiles", "player_world_state", "placed_cube_instances", "cube_fracture_pieces", "enemy_instances", "player_starter_grants"];
        return string.Join("\n", tables.Select(t => t + ":" + string.Join(";", Rows(c, $"SELECT {(t == "enemy_instances" ? "world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp, strength, agility, vitality, energy, movement_speed, attack_speed, level, created_utc, updated_utc" : "*")} FROM {t} ORDER BY 1, 2, 3;"))));
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
            command.CommandText = "CREATE TABLE added_after_item_rolls (id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
            throw new InvalidOperationException("migration failed after a partial change");
        }
    }
}
