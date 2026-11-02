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
    public void ARequestForTheStoredSize_ChangesNothing()
    {
        using MigrationTestDatabase db = new();
        Assert.Equal(56, ResolveDefault(db, 56).Descriptor.Width);
    }

    [Theory]
    [InlineData("INSERT INTO enemy_instances (world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp) VALUES (1, 20, 'Wolf', 'Wolf', 0, 0, 0, 0, 0, 1, 10, 10);")]
    [InlineData("INSERT INTO terrain_chunks (world_id, chunk_x, chunk_z, format_version, cell_count, data) VALUES (1, 2, 2, 1, 0, zeroblob(23));")]
    [InlineData(Profile + "INSERT INTO player_world_state (world_id, player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z) VALUES (1, 'alice', 1.5, 2, 3, 0, 0, 1);")]
    [InlineData(Profile + "INSERT INTO placed_cube_instances (world_id, object_id, owner_player_name, cell_x, cell_y, cell_z, material_id, current_health, max_health) VALUES (1, 10, 'alice', 4, 1, 5, 'stone', 5, 5);")]
    [InlineData("UPDATE worlds SET seed = '12345' WHERE world_id = 1;")]
    [InlineData("UPDATE worlds SET world_time_ticks = 777 WHERE world_id = 1;")]
    public void AWorldWithAnyData_KeepsItsSize(string sql)
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        db.ExecuteScript(sql);

        Assert.Equal(56, ResolveDefault(db, WorldDefaults.ServerNewWorldSize).Descriptor.Width);
        Assert.Equal(56, Column(db, "width"));
        Assert.Equal(56, Column(db, "depth"));
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
