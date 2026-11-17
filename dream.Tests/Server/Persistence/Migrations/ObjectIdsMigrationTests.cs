using Dream.Multiplayer.Diagnostics;
using Dream.Server.Persistence.Migrations;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// Persistence wave C (A): migration 0005 gives every cube, rubble piece and enemy a per-world
// object id, renumbered deterministically from the v4 ids, and keeps a counter (worlds.next_object_id)
// that four AFTER INSERT triggers keep above every inserted id.
public sealed class ObjectIdsMigrationTests
{
    private static readonly string FixturePath = Path.Combine(
        Dream.Tests.Integration.RepoRoot.Find(), "dream.Tests", "Server", "Persistence", "Migrations", "Fixtures",
        "v4-with-data.sql");

    private static SqliteSchemaMigrator UpToV4() =>
        new([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(4)]);

    private static MigrationTestDatabase V4WithData()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema(UpToV4());
        database.ExecuteScript(File.ReadAllText(FixturePath));
        Assert.Equal(4L, database.Scalar("SELECT MAX(version) FROM schema_version;"));
        return database;
    }

    [Fact]
    public void V4DatabaseWithData_MigratesToDeterministicIds_AndTheExpectedCounter()
    {
        using MigrationTestDatabase database = V4WithData();

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(4, result.FromVersion);
        Assert.Equal(MigrationTestDatabase.ShippedLatestVersion, result.ToVersion);
        using SqliteConnection c = database.Open();
        // Cubes in cube_id order after the items; the cube and the enemy that shared 2003 now differ.
        Assert.Equal(["43|alice|1", "44|alice|5", "45|bob|3"], Rows(c, "SELECT object_id || '|' || owner_player_name || '|' || cell_x FROM placed_cube_instances WHERE world_id = 1 ORDER BY object_id;"));
        Assert.Equal(["51|22", "52|20"], Rows(c, "SELECT object_id || '|' || CAST(position_x AS INTEGER) FROM enemy_instances WHERE world_id = 1 ORDER BY object_id;"));
        // Origins are the retired cube ids' groups; pieces follow in (source, index) order.
        Assert.Equal(["48|46|0", "49|47|0", "50|47|1"], Rows(c, "SELECT object_id || '|' || origin_object_id || '|' || piece_index FROM cube_fracture_pieces WHERE world_id = 1 ORDER BY object_id;"));
        Assert.Equal(53L, Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 1;"));
        // A second world numbers from 1 on its own.
        Assert.Equal(["1"], Rows(c, "SELECT object_id FROM placed_cube_instances WHERE world_id = 2;"));
        Assert.Equal(2L, Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 2;"));
        // Items keep their ids.
        Assert.Equal(["40", "41", "42"], Rows(c, "SELECT item_instance_id FROM item_instances ORDER BY item_instance_id;"));
        Assert.Equal("ok", Scalar(c, "PRAGMA integrity_check;"));
        Assert.Empty(Rows(c, "PRAGMA foreign_key_check;"));
        // The four counter triggers of 0005 exist (0006 adds a fifth for terrain overrides).
        Assert.Equal(
            ["trg_cube_fracture_pieces_advance_object_id", "trg_enemy_instances_advance_object_id", "trg_item_instances_advance_object_id", "trg_placed_cube_instances_advance_object_id", "trg_terrain_cell_overrides_advance_object_id"],
            Rows(c, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name LIKE '%advance_object_id' ORDER BY name;"));
    }

    [Fact]
    public void Migration_IsDeterministic_TwoRunsGiveTheSameRows()
    {
        using MigrationTestDatabase a = V4WithData();
        using MigrationTestDatabase b = V4WithData();
        a.Schema.EnsureSchema();
        b.Schema.EnsureSchema();

        const string dump =
            "SELECT group_concat(world_id || ':' || object_id || ':' || COALESCE(owner_player_name, '-') || ':' || cell_x, ',') FROM (SELECT * FROM placed_cube_instances ORDER BY world_id, object_id) " +
            "UNION ALL SELECT group_concat(world_id || ':' || object_id || ':' || origin_object_id || ':' || piece_index, ',') FROM (SELECT * FROM cube_fracture_pieces ORDER BY world_id, object_id) " +
            "UNION ALL SELECT group_concat(world_id || ':' || object_id || ':' || position_x, ',') FROM (SELECT * FROM enemy_instances ORDER BY world_id, object_id) " +
            "UNION ALL SELECT group_concat(world_id || ':' || next_object_id, ',') FROM (SELECT * FROM worlds ORDER BY world_id);";
        using SqliteConnection ca = a.Open();
        using SqliteConnection cb = b.Open();
        Assert.Equal(Rows(ca, dump), Rows(cb, dump));
        Assert.Equal(4, Rows(ca, dump).Count);
    }

    [Fact]
    public void Migration_KeepsTheInventorySlots_AndTheirItems()
    {
        using MigrationTestDatabase database = V4WithData();

        database.Schema.EnsureSchema();

        Assert.Equal(3L, database.Scalar("SELECT COUNT(*) FROM player_inventory_slots;"));
        Assert.Equal(3L, database.Scalar(
            "SELECT COUNT(*) FROM player_inventory_slots s JOIN item_instances i " +
            "ON i.world_id = s.world_id AND i.item_instance_id = s.item_instance_id;"));
    }

    [Fact]
    public void OrphanRow_FailsTheMigrationLoudly_AndTheVersionStaysAtFour()
    {
        using MigrationTestDatabase database = V4WithData();
        database.ExecuteScript("""
            PRAGMA foreign_keys = OFF;
            INSERT INTO placed_cube_instances (world_id, cube_id, owner_player_name, cell_x, cell_y, cell_z, material_id, current_health, max_health)
                VALUES (1, 3000, 'ghost', 1, 1, 1, 'Stone', 4, 5);
            """);
        SqlitePools.Release(database.DatabasePath);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => database.Schema.EnsureSchema());

        Assert.Contains("placed_cube_instances", error.Message, StringComparison.Ordinal);
        Assert.Contains("rolled back", error.Message, StringComparison.Ordinal);
        using SqliteConnection c = database.Open();
        Assert.Equal(4L, Scalar(c, "SELECT MAX(version) FROM schema_version;"));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('worlds') WHERE name = 'next_object_id';"));
        Assert.Equal(5L, Scalar(c, "SELECT COUNT(*) FROM placed_cube_instances;"));
    }

    [Fact]
    public void Triggers_RaiseTheCounter_AndNeverLowerIt()
    {
        using MigrationTestDatabase database = V4WithData();
        database.Schema.EnsureSchema();
        using SqliteConnection c = database.Open();

        InsertCube(c, 1, 100);
        Assert.Equal(101L, Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 1;"));
        InsertCube(c, 1, 7);
        Assert.Equal(101L, Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 1;"));

        MigrationTestDatabase.Execute(c, """
            INSERT INTO enemy_instances (world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp)
                VALUES (1, 200, 'wolf', 'Wolf', 1, 1, 1, 0, 0, 1, 5, 5);
            """);
        Assert.Equal(201L, Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 1;"));
        MigrationTestDatabase.Execute(c, """
            INSERT INTO cube_fracture_pieces (world_id, object_id, origin_object_id, piece_index, owner_player_name, parent_cell_x, parent_cell_y, parent_cell_z, center_x, center_y, center_z, side_length, material_id)
                VALUES (1, 300, 150, 0, NULL, 1, 1, 1, 1, 1, 1, 0.5, 'Stone');
            """);
        Assert.Equal(301L, Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 1;"));
        MigrationTestDatabase.Execute(c, """
            INSERT INTO item_instances (world_id, item_instance_id, item_kind, display_name, visual_id, state)
                VALUES (1, 400, 'tool.pick', 'Pick', 'pick', 'World');
            """);
        Assert.Equal(401L, Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 1;"));

        // Deleting the highest row leaves the counter where it was: ids are never reused.
        MigrationTestDatabase.Execute(c, "DELETE FROM item_instances WHERE item_instance_id = 400;");
        Assert.Equal(401L, Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 1;"));
        // Another world's counter is untouched.
        Assert.Equal(2L, Scalar(c, "SELECT next_object_id FROM worlds WHERE world_id = 2;"));
    }

    [Fact]
    public void AnUpsertThatUpdatesAnExistingRow_LeavesTheCounterAlone()
    {
        using MigrationTestDatabase database = V4WithData();
        database.Schema.EnsureSchema();
        SqlitePlacedObjectRepository cubes = new(new SqliteWorldStore(database.Schema), WorldId.Default);
        PersistedPlacedCube cube = new(
            new ObjectId(200), "alice", new Vector3(3, 1, 3), GeometryShapeKind.SolidCube, GeometryMaterialId.Stone,
            5, 5, DateTime.UtcNow, DateTime.UtcNow);

        cubes.SavePlacedCube(cube);
        Assert.Equal(201L, database.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 1;"));

        database.ExecuteScript("UPDATE worlds SET next_object_id = 5000 WHERE world_id = 1;");
        cubes.SavePlacedCube(cube with { CurrentHealth = 2 });

        Assert.Equal(5000L, database.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 1;"));
        Assert.Equal(2L, database.Scalar("SELECT current_health FROM placed_cube_instances WHERE world_id = 1 AND object_id = 200;"));
    }

    [Fact]
    public void DeletingAProfile_SetsTheOwnerToNullOnItsCubesAndRubble_WhileItsSlotsCascadeAway()
    {
        using MigrationTestDatabase database = V4WithData();
        database.Schema.EnsureSchema();
        using SqliteConnection c = database.Open();
        MigrationTestDatabase.Execute(c, "PRAGMA foreign_keys = ON;");

        MigrationTestDatabase.Execute(c, "DELETE FROM player_profiles WHERE player_name = 'bob';");

        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM placed_cube_instances WHERE world_id = 1 AND object_id = 45 AND owner_player_name IS NULL;"));
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM placed_cube_instances WHERE world_id = 2 AND object_id = 1 AND owner_player_name IS NULL;"));
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1 AND object_id = 48 AND owner_player_name IS NULL;"));
        Assert.Equal(4L, Scalar(c, "SELECT COUNT(*) FROM placed_cube_instances;"));
        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM player_inventory_slots WHERE player_name = 'bob';"));
        Assert.Equal(2L, Scalar(c, "SELECT COUNT(*) FROM player_inventory_slots WHERE player_name = 'alice';"));
    }

    [Fact]
    public void AnOwnerlessCube_IsAcceptedButABlankOwnerIsNot()
    {
        using MigrationTestDatabase database = V4WithData();
        database.Schema.EnsureSchema();
        using SqliteConnection c = database.Open();

        MigrationTestDatabase.Execute(c, """
            INSERT INTO placed_cube_instances (world_id, object_id, owner_player_name, cell_x, cell_y, cell_z, material_id, current_health, max_health)
                VALUES (1, 60, NULL, 1, 1, 1, 'Stone', 4, 5);
            """);
        Assert.Throws<SqliteException>(() => MigrationTestDatabase.Execute(c, """
            INSERT INTO placed_cube_instances (world_id, object_id, owner_player_name, cell_x, cell_y, cell_z, material_id, current_health, max_health)
                VALUES (1, 61, '  ', 1, 1, 1, 'Stone', 4, 5);
            """));
    }

    [Fact]
    public void HealGuard_RaisesAStaleCounter_AndLogsThatItDid()
    {
        using MigrationTestDatabase database = V4WithData();
        database.Schema.EnsureSchema();
        database.ExecuteScript("UPDATE worlds SET next_object_id = 3 WHERE world_id = 1;");
        SqliteObjectIdCounterRepository counter = new(new SqliteWorldStore(database.Schema), WorldId.Default);
        List<(string Category, string Message)> lines = [];

        long next;
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            next = counter.LoadNextObjectId();
        }

        Assert.Equal(53L, next);
        Assert.Equal(53L, database.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 1;"));
        (string category, string message) = Assert.Single(lines);
        Assert.Equal("server.persistence.object-id-counter-healed", category);
        Assert.Contains("stored=3", message, StringComparison.Ordinal);
        Assert.Contains("healed=53", message, StringComparison.Ordinal);
    }

    [Fact]
    public void HealGuard_LeavesAHealthyCounterAlone_AndStaysSilent()
    {
        using MigrationTestDatabase database = V4WithData();
        database.Schema.EnsureSchema();
        database.ExecuteScript("UPDATE worlds SET next_object_id = 900 WHERE world_id = 1;");
        SqliteObjectIdCounterRepository counter = new(new SqliteWorldStore(database.Schema), WorldId.Default);
        List<(string Category, string Message)> lines = [];

        long next;
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            next = counter.LoadNextObjectId();
        }

        Assert.Equal(900L, next);
        Assert.Empty(lines);
        Assert.Equal(900L, database.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 1;"));
    }

    private static void InsertCube(SqliteConnection c, long world, long objectId) =>
        MigrationTestDatabase.Execute(c, $"""
            INSERT INTO placed_cube_instances (world_id, object_id, owner_player_name, cell_x, cell_y, cell_z, material_id, current_health, max_health)
                VALUES ({world}, {objectId}, 'alice', 1, 1, 1, 'Stone', 4, 5);
            """);

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
            rows.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return rows;
    }
}
