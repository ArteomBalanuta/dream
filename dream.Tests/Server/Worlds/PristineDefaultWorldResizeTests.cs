using Xunit;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Server.Worlds;

// M9: migration 0003 seeds world 1 at 56 x 8 x 56 on every database, so a fresh database's first server start would
// never make the 100 m world it asks for. A pristine world 1 is resized to the requested size; a world with any data
// is never touched. Real migrations on a real temp database.
public sealed class PristineDefaultWorldResizeTests
{
    private const string Profile = "INSERT INTO player_profiles (player_name, display_name) VALUES ('alice', 'Alice'); ";

    private static WorldRecord ResolveDefault(MigrationTestDatabase db, int size)
    {
        db.Schema.EnsureSchema();
        return new SqliteWorldCatalog(db.Schema).Resolve(WorldSelector.Default, size, out _);
    }

    private static long Column(MigrationTestDatabase db, string column) =>
        Convert.ToInt64(db.Scalar($"SELECT {column} FROM worlds WHERE world_id = 1"));

    [Fact]
    public void AFreshDatabase_ResizesWorldOneToTheRequestedSize()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        Assert.Equal(56, Column(db, "width"));

        WorldRecord record = ResolveDefault(db, WorldDefaults.ServerNewWorldSize);

        Assert.Equal(WorldId.Default, record.Descriptor.Id);
        Assert.Equal(100, record.Descriptor.Width);
        Assert.Equal(100, record.Descriptor.Depth);
        Assert.Equal(WorldDescriptor.DefaultHeight, record.Descriptor.Height);
        Assert.Equal(100, Column(db, "width"));
        Assert.Equal(100, Column(db, "depth"));
        Assert.Equal(8, Column(db, "height"));
    }

    [Fact]
    public void ARequestForTheStoredSize_KeepsTheSize()
    {
        using MigrationTestDatabase db = new();
        Assert.Equal(56, ResolveDefault(db, 56).Descriptor.Width);
    }

    // The migration seeds world 1 with the classic key; a pristine world 1 moves to the new-world generator,
    // at the stored size too, so a fresh database's first world is generated like every other new world.
    [Theory]
    [InlineData(56)]
    [InlineData(WorldDefaults.ServerNewWorldSize)]
    public void AFreshDatabase_MovesWorldOneToTheNewWorldGenerator(int size)
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        Assert.Equal(WorldGeneratorCatalog.DefaultKey, (string)db.Scalar("SELECT generator_key FROM worlds WHERE world_id = 1")!);

        WorldRecord record = ResolveDefault(db, size);

        Assert.Equal(WorldGeneratorCatalog.NewWorldKey, record.Descriptor.GeneratorKey);
        Assert.Equal(WorldGeneratorCatalog.NaturalVersion, record.Descriptor.GeneratorVersion);
        Assert.Equal(WorldGeneratorCatalog.NewWorldKey, (string)db.Scalar("SELECT generator_key FROM worlds WHERE world_id = 1")!);
        Assert.Equal(WorldGeneratorCatalog.NaturalVersion, Column(db, "generator_version"));
        record.Descriptor.Validate();
    }

    [Theory]
    [InlineData("INSERT INTO enemy_instances (world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp) VALUES (1, 20, 'Wolf', 'Wolf', 0, 0, 0, 0, 0, 1, 10, 10);")]
    [InlineData("INSERT INTO terrain_chunks (world_id, chunk_x, chunk_z, format_version, cell_count, data) VALUES (1, 2, 2, 1, 0, zeroblob(23));")]
    [InlineData(Profile + "INSERT INTO player_world_state (world_id, player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z) VALUES (1, 'alice', 1.5, 2, 3, 0, 0, 1);")]
    [InlineData(Profile + "INSERT INTO placed_cube_instances (world_id, object_id, owner_player_name, cell_x, cell_y, cell_z, material_id, current_health, max_health) VALUES (1, 10, 'alice', 4, 1, 5, 'stone', 5, 5);")]
    [InlineData("UPDATE worlds SET seed = '12345' WHERE world_id = 1;")]
    [InlineData("UPDATE worlds SET world_time_ticks = 777 WHERE world_id = 1;")]
    public void AWorldWithAnyData_KeepsItsSizeAndGenerator(string sql)
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        db.ExecuteScript(sql);

        WorldRecord record = ResolveDefault(db, WorldDefaults.ServerNewWorldSize);
        Assert.Equal(56, record.Descriptor.Width);
        Assert.Equal(WorldGeneratorCatalog.DefaultKey, record.Descriptor.GeneratorKey);
        Assert.Equal(56, Column(db, "width"));
        Assert.Equal(56, Column(db, "depth"));
        Assert.Equal(WorldGeneratorCatalog.DefaultKey, (string)db.Scalar("SELECT generator_key FROM worlds WHERE world_id = 1")!);
    }

    [Fact]
    public void AStoredWorld_KeepsItsSize()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        db.ExecuteScript("UPDATE worlds SET map_source = 'stored', seed = '9' WHERE world_id = 1;");

        Assert.Equal(56, ResolveDefault(db, WorldDefaults.ServerNewWorldSize).Descriptor.Width);
        Assert.Equal(56, Column(db, "width"));
    }
}
