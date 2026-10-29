using Dream.Server.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// Migration 0004 drops the updated_utc indexes of the three busy tables (checkpoint UPDATEs set
// updated_utc on every row, and no query reads the index). Nothing else changes.
public sealed class DropBusyTableIndexesMigrationTests
{
    private static readonly string[] DroppedIndexes =
    [
        "ix_cube_fracture_pieces_updated_utc", "ix_enemy_instances_updated_utc", "ix_placed_cube_instances_updated_utc"
    ];

    [Fact]
    public void Migration_DropsOnlyTheThreeBusyTableIndexes_AndKeepsEveryRow()
    {
        using MigrationTestDatabase database = new();
        SqliteSchemaMigrator upToTheWorlds = new([.. SchemaMigrations.LoadFromDirectory(MigrationTestDatabase.MigrationsDirectory).Take(3)]);
        database.Schema.EnsureSchema(upToTheWorlds);
        database.ExecuteScript("""
            INSERT INTO player_profiles (player_name, display_name) VALUES ('alice', 'Alice');
            INSERT INTO placed_cube_instances (world_id, cube_id, owner_player_name, cell_x, cell_y, cell_z, material_id, current_health, max_health)
            VALUES (1, 7, 'alice', 1, 2, 3, 'Stone', 4, 5);
            INSERT INTO enemy_instances (world_id, enemy_id, enemy_kind, display_name, position_x, position_y, position_z,
                facing_x, facing_y, facing_z, current_hp, max_hp)
            VALUES (1, 3, 'Goblin', 'Goblin', 4, 5, 6, 1, 0, 0, 10, 20);
            INSERT INTO cube_fracture_pieces (world_id, source_cube_id, piece_index, owner_player_name,
                parent_cell_x, parent_cell_y, parent_cell_z, center_x, center_y, center_z, side_length, material_id)
            VALUES (1, 8, 0, 'alice', 1, 2, 3, 1.25, 2.25, 3.25, 0.5, 'Stone');
            """);
        Assert.Equal(DroppedIndexes.Length, IndexCount(database, DroppedIndexes));

        SchemaMigrationResult result = database.Schema.EnsureSchema();

        Assert.Equal(3, result.FromVersion);
        Assert.Equal(4, result.ToVersion);
        Assert.Equal(0L, IndexCount(database, DroppedIndexes));
        // The other updated_utc indexes are not on a hot write path and stay.
        Assert.Equal(3L, IndexCount(database, ["ix_item_instances_updated_utc", "ix_player_world_state_updated_utc", "ix_player_profiles_updated_utc"]));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM placed_cube_instances WHERE world_id = 1 AND cube_id = 7;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM enemy_instances WHERE world_id = 1 AND enemy_id = 3;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1 AND source_cube_id = 8;"));
    }

    [Fact]
    public void FreshDatabase_HasNoneOfTheDroppedIndexes()
    {
        using MigrationTestDatabase database = new();

        database.Schema.EnsureSchema();

        Assert.Equal(0L, IndexCount(database, DroppedIndexes));
    }

    private static long IndexCount(MigrationTestDatabase database, string[] names) =>
        (long)database.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('" + string.Join("','", names) + "');")!;
}
