using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Server.Persistence.Migrations;

// Persistence wave B x wave A: a schema version 2 database that has been played on (every
// world-scoped table has rows, the seed and the world time are in world_metadata) is opened by the
// production bootstrap. Migration 0003 runs with a backup, every row lands in world 1, the seed and
// the clock move onto the worlds row, and the server starts on the restored world.
public sealed class V2DatabaseWithDataStartupTests(ITestOutputHelper output)
{
    private static readonly string[] WorldTables =
    [
        "player_world_state", "placed_cube_instances", "cube_fracture_pieces", "enemy_instances",
        "item_instances", "player_inventory_slots", "player_starter_grants"
    ];

    private static readonly long[] FixtureRowCounts = [1, 2, 2, 1, 4, 4, 1];

    [Fact]
    public void V2DatabaseWithData_MigratesToV3_KeepsEveryRow_AndTheServerStartsOnWorld1()
    {
        using MigrationTestDatabase database = new();
        BuildV2DatabaseWithData(database);
        for (int i = 0; i < WorldTables.Length; i++)
        {
            Assert.Equal(FixtureRowCounts[i], database.Scalar($"SELECT COUNT(*) FROM {WorldTables[i]};"));
        }

        Assert.Equal(2L, database.Scalar("SELECT MAX(version) FROM schema_version;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'worlds';"));

        List<string> log = [];
        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(
            database.DatabasePath, MigrationTestDatabase.InitScriptPath, log: log.Add);
        foreach (string line in log)
        {
            output.WriteLine(line);
        }

        // One backup of the v2 file, taken before 0003 ran.
        string backup = Assert.Single(database.Backups());
        Assert.Matches(@"persistence\.sqlite\.v0002-to-v0004\.\d{8}T\d{6}Z\.bak$", backup);
        Assert.Equal(4L, database.Scalar("SELECT MAX(version) FROM schema_version;"));

        // Every row moved to world 1; nothing was lost, nothing went to another world.
        for (int i = 0; i < WorldTables.Length; i++)
        {
            Assert.Equal(FixtureRowCounts[i], database.Scalar($"SELECT COUNT(*) FROM {WorldTables[i]} WHERE world_id = 1;"));
            Assert.Equal(FixtureRowCounts[i], database.Scalar($"SELECT COUNT(*) FROM {WorldTables[i]};"));
        }

        // The slots still point at their items (the table rebuilds must not cascade them away).
        Assert.Equal(4L, database.Scalar(
            "SELECT COUNT(*) FROM player_inventory_slots s JOIN item_instances i " +
            "ON i.world_id = s.world_id AND i.item_instance_id = s.item_instance_id;"));

        // The seed and the clock moved onto the worlds row and out of world_metadata.
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM worlds;"));
        Assert.Equal("default", database.Scalar("SELECT name FROM worlds WHERE world_id = 1;"));
        Assert.Equal("5150", database.Scalar("SELECT seed FROM worlds WHERE world_id = 1;"));
        Assert.Equal(50_000L, database.Scalar("SELECT world_time_ticks FROM worlds WHERE world_id = 1;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM world_metadata WHERE key IN ('simulation_seed', 'world_time_ticks');"));
        Assert.Null(database.Scalar("PRAGMA foreign_key_check;"));
        Assert.Equal("ok", database.Scalar("PRAGMA integrity_check;"));

        Assert.Equal(WorldId.Default, bundle.WorldId);
        Assert.Equal(5150UL, bundle.SimulationSeed);
        Assert.Equal(50_000L, bundle.WorldTimeTicks);

        // The server starts on the restored world: the same cubes, rubble and wolf, the stored seed.
        using (DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle))
        {
            host.Runtime!.WriteCheckpointOnDispose = false;
            Assert.Equal(5150UL, host.Runtime.SimulationSeed);
            Assert.Equal(GeometryMaterialId.Stone, host.Runtime.Map.TryGetCubeAtCell(30, 1, 20)?.MaterialId);
            Assert.Equal(GeometryMaterialId.Lamp, host.Runtime.Map.TryGetCubeAtCell(32, 1, 20)?.MaterialId);
            Assert.Contains(host.Runtime.Map.CaptureFracturePieces(), piece => piece.Cell == new GeometryCellKey(24, 1, 20));
            Assert.True(host.Runtime.Enemies.TryGetEnemyByActorId(WorldSessionRuntime.FirstEnemyActorId, out _));
            Assert.Equal(50_000L, host.Runtime.WorldClock.WorldTicks);
        }

        // A second start migrates nothing and takes no second backup.
        SqliteConnection.ClearAllPools();
        using (ServerPersistenceBundle reopened = ServerBootstrap.CreatePersistenceBundle(
            database.DatabasePath, MigrationTestDatabase.InitScriptPath))
        {
            Assert.Equal(5150UL, reopened.SimulationSeed);
        }

        SqliteConnection.ClearAllPools();
        Assert.Single(database.Backups());
    }

    // The v2 schema is the shipped 0001 and 0002 migrations only; the fixture adds the rows.
    private static void BuildV2DatabaseWithData(MigrationTestDatabase database)
    {
        IReadOnlyList<ISchemaMigration> shipped = SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory);
        SqliteSchemaMigrator v2Only = new([.. shipped.Take(2)]);
        SchemaMigrationResult result = database.Schema.EnsureSchema(v2Only);
        Assert.Equal(new SchemaMigrationResult(0, 2, null), result);
        MigrationTestDatabase.ReleasePooledConnections();
        database.ExecuteScript(File.ReadAllText(Path.Combine(
            Dream.Tests.Integration.RepoRoot.Find(), "dream.Tests", "Server", "Persistence", "Migrations", "Fixtures", "v2-with-data.sql")));
        MigrationTestDatabase.ReleasePooledConnections();
    }
}
