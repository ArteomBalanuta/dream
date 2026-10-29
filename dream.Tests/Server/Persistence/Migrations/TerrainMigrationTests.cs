using System.Globalization;
using Dream.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// Persistence wave D (A): migration 0006 adds terrain_cell_overrides (with its own counter trigger,
// the fifth) and terrain_chunks. New tables only.
public sealed class TerrainMigrationTests
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

    public static TheoryData<int> FromVersions => new() { 0, 1, 2, 3, 4, 5 };

    [Theory]
    [MemberData(nameof(FromVersions))]
    public void EveryEarlierVersion_MigratesToSix_Clean(int fromVersion)
    {
        using MigrationTestDatabase database = new();
        if (fromVersion == 0)
        {
            database.BuildWithPreMigrationBootstrapper();
        }
        else
        {
            database.Schema.EnsureSchema(UpTo(fromVersion));
            Assert.Equal((long)fromVersion, database.Scalar("SELECT MAX(version) FROM schema_version;"));
        }

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(fromVersion, result.FromVersion);
        Assert.Equal(6, result.ToVersion);
        Assert.Equal(6, MigrationTestDatabase.ShippedLatestVersion);
        using SqliteConnection c = database.Open();
        Assert.Equal("ok", Scalar(c, "PRAGMA integrity_check;"));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(Triggers, Rows(c, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name LIKE '%advance_object_id' ORDER BY name;"));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM terrain_cell_overrides;"));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM terrain_chunks;"));
    }

    [Fact]
    public void V5DatabaseWithData_KeepsEveryRowAndTheCounter()
    {
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpTo(4));
        database.ExecuteScript(File.ReadAllText(Path.Combine(
            Dream.Tests.Integration.RepoRoot.Find(), "dream.Tests", "Server", "Persistence", "Migrations", "Fixtures",
            "v4-with-data.sql")));
        database.Schema.EnsureSchema(UpTo(5));
        string before = Dump(database);
        object? counter = database.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 1;");

        Assert.Equal(6, database.Schema.EnsureSchema().ToVersion);

        Assert.Equal(before, Dump(database));
        Assert.Equal(counter, database.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 1;"));
    }

    [Theory]
    [InlineData(0, 3, 2, 4, 0, "NULL")]
    [InlineData(5, -1, 2, 4, 0, "NULL")]
    [InlineData(5, 3, 0, 4, 0, "NULL")]
    [InlineData(5, 3, -1, 4, 0, "NULL")]
    [InlineData(5, 3, 2, -1, 0, "NULL")]
    [InlineData(5, 3, 2, 4, 2, "NULL")]
    [InlineData(5, 3, 2, 4, 0, "x'00'")]
    [InlineData(5, 3, 2, 4, 1, "NULL")]
    public void OverrideChecks_RejectBadRows(int objectId, int x, int y, int z, int kind, string cellData)
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();

        SqliteException error = Assert.Throws<SqliteException>(() => MigrationTestDatabase.Execute(
            c,
            $"INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind, cell_data) VALUES (1, {objectId}, {x}, {y}, {z}, {kind}, {cellData});"));
        Assert.Equal(ConstraintError, error.SqliteErrorCode);
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM terrain_cell_overrides;"));
    }

    [Fact]
    public void GoodOverrides_AreAccepted_RemovedWithoutDataAndReplacedWithData()
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();

        MigrationTestDatabase.Execute(c, "INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind) VALUES (1, 5, 1, 1, 1, 0);");
        MigrationTestDatabase.Execute(c, "INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind, cell_data) VALUES (1, 6, 2, 1, 1, 1, x'01');");

        Assert.Equal(2L, Scalar(c, "SELECT COUNT(*) FROM terrain_cell_overrides;"));
    }

    [Fact]
    public void OverrideCell_IsUniquePerWorld_AndTheObjectIdIsTheKey()
    {
        using MigrationTestDatabase database = Latest();
        AddWorldTwo(database);
        using SqliteConnection c = database.Open();
        MigrationTestDatabase.Execute(c, "INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind) VALUES (1, 5, 1, 1, 1, 0);");

        Assert.Throws<SqliteException>(() => MigrationTestDatabase.Execute(c, "INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind) VALUES (1, 6, 1, 1, 1, 0);"));
        Assert.Throws<SqliteException>(() => MigrationTestDatabase.Execute(c, "INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind) VALUES (1, 5, 9, 1, 1, 0);"));
        // The same cell and id in another world is fine.
        MigrationTestDatabase.Execute(c, "INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind) VALUES (2, 5, 1, 1, 1, 0);");
    }

    [Fact]
    public void OverrideInsert_RaisesTheCounter_AndAnIgnoredDuplicateDoesNot()
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();
        Assert.Equal(1L, Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 1;"));

        MigrationTestDatabase.Execute(c, "INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind) VALUES (1, 50, 1, 1, 1, 0);");
        Assert.Equal(51L, Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 1;"));

        // A lower id never lowers the counter.
        MigrationTestDatabase.Execute(c, "INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind) VALUES (1, 7, 2, 1, 1, 0);");
        Assert.Equal(51L, Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 1;"));

        MigrationTestDatabase.Execute(c, "INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind) VALUES (1, 900, 1, 1, 1, 0) ON CONFLICT(world_id, cell_x, cell_y, cell_z) DO NOTHING;");
        Assert.Equal(51L, Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 1;"));
        Assert.Equal(2L, Scalar(c, "SELECT COUNT(*) FROM terrain_cell_overrides;"));
    }

    [Theory]
    [InlineData(-1, 0, 1, 0, 23)]
    [InlineData(0, -1, 1, 0, 23)]
    [InlineData(0, 0, 0, 0, 23)]
    [InlineData(0, 0, 1, -1, 23)]
    [InlineData(0, 0, 1, 0, 22)]
    public void ChunkChecks_RejectBadRows(int chunkX, int chunkZ, int formatVersion, int cellCount, int dataLength)
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();

        SqliteException error = Assert.Throws<SqliteException>(() => MigrationTestDatabase.Execute(
            c,
            $"INSERT INTO terrain_chunks (world_id, chunk_x, chunk_z, format_version, cell_count, data) VALUES (1, {chunkX}, {chunkZ}, {formatVersion}, {cellCount}, zeroblob({dataLength}));"));
        Assert.Equal(ConstraintError, error.SqliteErrorCode);

        // The minimum good row is accepted.
        MigrationTestDatabase.Execute(c, "INSERT INTO terrain_chunks (world_id, chunk_x, chunk_z, format_version, cell_count, data) VALUES (1, 0, 0, 1, 0, zeroblob(23));");
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM terrain_chunks;"));
    }

    [Fact]
    public void ChunksTable_IsWithoutRowid_AndKeyedByWorldAndChunk()
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();

        Assert.Contains("WITHOUT ROWID", (string)Scalar(c, "SELECT sql FROM sqlite_master WHERE name = 'terrain_chunks';")!, StringComparison.OrdinalIgnoreCase);
        MigrationTestDatabase.Execute(c, "INSERT INTO terrain_chunks (world_id, chunk_x, chunk_z, format_version, cell_count, data) VALUES (1, 0, 0, 1, 0, zeroblob(23));");
        Assert.Throws<SqliteException>(() => MigrationTestDatabase.Execute(c, "INSERT INTO terrain_chunks (world_id, chunk_x, chunk_z, format_version, cell_count, data) VALUES (1, 0, 0, 1, 0, zeroblob(23));"));
    }

    [Fact]
    public void DeletingAWorld_CascadesToOverridesAndChunks()
    {
        using MigrationTestDatabase database = Latest();
        AddWorldTwo(database);
        using SqliteConnection c = database.Open();
        MigrationTestDatabase.Execute(c, "PRAGMA foreign_keys = ON;");
        MigrationTestDatabase.Execute(c, "INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind) VALUES (2, 5, 1, 1, 1, 0), (1, 5, 1, 1, 1, 0);");
        MigrationTestDatabase.Execute(c, "INSERT INTO terrain_chunks (world_id, chunk_x, chunk_z, format_version, cell_count, data) VALUES (2, 0, 0, 1, 0, zeroblob(23)), (1, 0, 0, 1, 0, zeroblob(23));");

        MigrationTestDatabase.Execute(c, "DELETE FROM worlds WHERE world_id = 2;");

        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = 2;"));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM terrain_chunks WHERE world_id = 2;"));
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = 1;"));
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM terrain_chunks WHERE world_id = 1;"));
    }

    [Fact]
    public void OrphanRows_AreRejectedWhenForeignKeysAreOn()
    {
        using MigrationTestDatabase database = Latest();
        using SqliteConnection c = database.Open();
        MigrationTestDatabase.Execute(c, "PRAGMA foreign_keys = ON;");

        Assert.Throws<SqliteException>(() => MigrationTestDatabase.Execute(c, "INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind) VALUES (99, 5, 1, 1, 1, 0);"));
        Assert.Throws<SqliteException>(() => MigrationTestDatabase.Execute(c, "INSERT INTO terrain_chunks (world_id, chunk_x, chunk_z, format_version, cell_count, data) VALUES (99, 0, 0, 1, 0, zeroblob(23));"));
    }

    private static MigrationTestDatabase Latest()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        return database;
    }

    private static void AddWorldTwo(MigrationTestDatabase database) =>
        database.ExecuteScript(
            "INSERT INTO worlds (world_id, name, width, height, depth, map_source, generator_key, generator_version, seed, world_time_ticks, created_schema_version) " +
            "VALUES (2, 'two', 56, 8, 56, 'generated', 'default', 1, '1', 0, 6);");

    private static SqliteSchemaMigrator UpTo(int version) =>
        new([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(version)]);

    private static string Dump(MigrationTestDatabase database)
    {
        using SqliteConnection c = database.Open();
        string[] tables = ["worlds", "player_profiles", "placed_cube_instances", "cube_fracture_pieces", "enemy_instances", "item_instances", "player_inventory_slots"];
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
}
