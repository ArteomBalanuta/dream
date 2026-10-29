using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Server.Persistence.Repositories;

// Persistence wave B (B1): the repositories and the checkpoint writer are bound to one world. Two worlds
// can hold the same local ids (cube 1, enemy 1, item 1, ...) without seeing each other's rows.
public sealed class WorldScopedRepositoryTests
{
    private const string Owner = "alice";
    private static readonly DateTime Created = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly WorldId One = WorldId.Default;
    private static readonly WorldId Two = new(2);

    [Fact]
    public void TwoWorldsWithTheSameLocalIds_StayIsolated_ThroughEveryAggregate()
    {
        using PersistenceTestDatabase database = NewDatabaseWithTwoWorlds();
        SqliteWorldPersistenceRepository one = new(database.Schema, One);
        SqliteWorldPersistenceRepository two = new(database.Schema, Two);

        Fill(one, tag: 1);
        Fill(two, tag: 2);

        AssertFilled(one, tag: 1);
        AssertFilled(two, tag: 2);
        Assert.Equal(2L, database.Count("SELECT COUNT(*) FROM placed_cube_instances;"));
        Assert.Equal(3L, database.Count("SELECT COUNT(*) FROM item_instances;"));

        // Deletes and the slot reset stay inside their world.
        two.DeletePlacedCube(new ObjectId(1));
        two.DeleteCubeFracturePieces(new ObjectId(1));
        two.DeleteEnemyInstance(new ObjectId(1));
        two.DeleteItemInstance(1);
        two.DeleteInventorySlots(Owner);
        AssertFilled(one, tag: 1);
        Assert.Empty(two.LoadPlacedCubes());
        Assert.Empty(two.LoadCubeFracturePieces());
        Assert.Empty(two.LoadActiveEnemies());
        Assert.Empty(two.LoadItemInstances());
        Assert.Empty(two.LoadInventorySlots());

        Assert.Equal([1], one.LoadActiveEnemies().Select(enemy => (int)enemy.ObjectId.Value));
        two.SaveEnemyInstance(Enemy(1, 12));
        Assert.Equal([1], two.LoadActiveEnemies().Select(enemy => (int)enemy.ObjectId.Value));
        Assert.Equal(11, one.LoadActiveEnemies().Single().Stats.CurrentHealth);
    }

    [Fact]
    public void TheMetadataRepository_ReadsAndWritesItsOwnWorldsRow()
    {
        using PersistenceTestDatabase database = NewDatabaseWithTwoWorlds();
        SqliteWorldMetadataRepository one = new(database.Schema, One);
        SqliteWorldMetadataRepository two = new(database.Schema, Two);

        Assert.Equal(new WorldSimulationSeed(111UL, Created: true), one.GetOrCreateSimulationSeed(() => 111UL));
        Assert.Equal(new WorldSimulationSeed(222UL, Created: true), two.GetOrCreateSimulationSeed(() => 222UL));
        Assert.Equal(new WorldSimulationSeed(111UL, Created: false), one.GetOrCreateSimulationSeed(() => 999UL));

        new SqlitePersistenceCheckpointWriter(database.Schema, Two).Write(PersistenceCheckpoint.Empty with { WorldTimeTicks = 500 });
        Assert.False(one.TryGetWorldTimeTicks(out _));
        Assert.True(two.TryGetWorldTimeTicks(out long ticks));
        Assert.Equal(500L, ticks);
    }

    [Fact]
    public void TheWorldTwoCheckpointWriter_DoesNotTouchWorldOne()
    {
        using PersistenceTestDatabase database = NewDatabaseWithTwoWorlds();
        SqliteWorldPersistenceRepository one = new(database.Schema, One);
        SqliteWorldPersistenceRepository two = new(database.Schema, Two);
        Fill(one, tag: 1);
        Fill(two, tag: 2);
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        new SqliteWorldMetadataRepository(database.Schema, One).GetOrCreateSimulationSeed(() => 5UL);
        new SqlitePersistenceCheckpointWriter(database.Schema, One).Write(PersistenceCheckpoint.Empty with { WorldTimeTicks = 77 });

        DateTime later = Created.AddHours(1);
        PersistenceCheckpoint checkpoint = PersistenceCheckpoint.Empty with
        {
            PlayerWorldStates = [new PersistedPlayerWorldState(Owner, new Vector3(9f, 9f, 9f), new Vector3(0f, 0f, 1f), 50, 3, later)],
            PlayerVitals = [new PlayerCheckpointVitals(Owner, Dream.Simulation.Players.PlayerInteractionMode.Building, 10, 100)],
            Enemies = [Enemy(1, 3)],
            PlacedCubes = [Cube(1, health: 1)],
            FracturePieces = [Piece(1, 0)],
            WorldTimeTicks = 900
        };
        new SqlitePersistenceCheckpointWriter(database.Schema, Two).Write(checkpoint);

        // World 1 is exactly as it was.
        AssertFilled(one, tag: 1);
        Assert.Equal(Created, one.TryGetPlayerWorldState(Owner)!.Value.UpdatedUtc);
        Assert.Equal(77L, ReadTicks(database, 1));
        // World 2 took the checkpoint.
        Assert.Equal(1, two.LoadPlacedCubes().Single().CurrentHealth);
        Assert.Equal(3, two.LoadActiveEnemies().Single().Stats.CurrentHealth);
        Assert.Equal(new Vector3(9f, 9f, 9f), two.TryGetPlayerWorldState(Owner)!.Value.Position);
        Assert.Equal(later, two.TryGetPlayerWorldState(Owner)!.Value.UpdatedUtc);
        Assert.Equal(900L, ReadTicks(database, 2));
        Assert.Single(two.LoadCubeFracturePieces());
        _ = profiles;
    }

    [Fact]
    public void ACheckpointForAWorldWithoutARow_ThrowsInsteadOfDroppingItsClock()
    {
        using PersistenceTestDatabase database = new();
        SqlitePersistenceCheckpointWriter writer = new(database.Schema, new WorldId(7));

        Assert.Throws<InvalidOperationException>(() => writer.Write(PersistenceCheckpoint.Empty with { WorldTimeTicks = 1 }));
    }

    [Fact]
    public void TheWorldBoundConstructors_AcceptAnyWorldId()
    {
        using PersistenceTestDatabase database = new();

        _ = new SqliteWorldPersistenceRepository(database.Schema, new WorldId(42));
        _ = new SqlitePersistenceCheckpointWriter(database.Schema, new WorldId(42));
        _ = new SqliteWorldMetadataRepository(database.Schema, new WorldId(42));
    }

    [Fact]
    public void WorldBoundWrites_StillEnlistInTheBatch_AndCommitOnce()
    {
        using PersistenceTestDatabase database = NewDatabaseWithTwoWorlds();
        SqliteWorldPersistenceRepository two = new(database.Schema, Two);

        using SqliteWriteBatch batch = new();
        batch.Activate();
        try
        {
            batch.BeginWrite();
            two.SavePlacedCube(Cube(1, 5));
            two.SavePlacedCube(Cube(2, 5));
            two.SaveEnemyInstance(Enemy(1, 10));
            two.SaveItemInstance(Item(1));
            two.SaveInventorySlot(new PersistedInventorySlot(Owner, 0, 1, Created));
            batch.CompleteWrite();
            // On the batch's transaction: another connection sees nothing yet.
            Assert.Equal(0L, database.Count("SELECT COUNT(*) FROM placed_cube_instances WHERE world_id = 2;"));
            batch.Commit();
        }
        finally
        {
            SqliteWriteBatch.Deactivate();
        }

        Assert.Equal(1L, batch.CommittedTransactions);
        Assert.Equal(2L, database.Count("SELECT COUNT(*) FROM placed_cube_instances WHERE world_id = 2;"));
        Assert.Equal(0L, database.Count("SELECT COUNT(*) FROM placed_cube_instances WHERE world_id = 1;"));
        Assert.Equal(1L, database.Count("SELECT COUNT(*) FROM player_inventory_slots WHERE world_id = 2;"));
    }

    [Fact]
    public void AnInventorySlotCannotPointAtAnotherWorldsItem()
    {
        using PersistenceTestDatabase database = NewDatabaseWithTwoWorlds();
        new SqliteWorldPersistenceRepository(database.Schema, One).SaveItemInstance(Item(1));
        SqliteWorldPersistenceRepository two = new(database.Schema, Two);

        // Item 1 exists in world 1 only: the (world_id, item_instance_id) foreign key refuses the slot.
        Assert.Throws<SqliteException>(() => two.SaveInventorySlot(new PersistedInventorySlot(Owner, 0, 1, Created)));
    }

    [Fact]
    public void DeletingAWorldRow_CascadesToItsRowsOnly()
    {
        using PersistenceTestDatabase database = NewDatabaseWithTwoWorlds();
        SqliteWorldPersistenceRepository one = new(database.Schema, One);
        SqliteWorldPersistenceRepository two = new(database.Schema, Two);
        Fill(one, tag: 1);
        Fill(two, tag: 2);

        using (SqliteConnection connection = database.Schema.CreateOpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM worlds WHERE world_id = 2;";
            command.ExecuteNonQuery();
        }

        AssertFilled(one, tag: 1);
        Assert.Empty(two.LoadPlacedCubes());
        Assert.Empty(two.LoadInventorySlots());
        Assert.Empty(two.LoadItemInstances());
    }

    private static void Fill(SqliteWorldPersistenceRepository world, int tag)
    {
        // The same local ids in every world; the health/hp tag tells the worlds apart.
        world.SavePlayerWorldState(new PersistedPlayerWorldState(Owner, new Vector3(tag, 2f, 3f), new Vector3(0f, 0f, -1f), 40 + tag, 7, Created));
        world.SavePlacedCube(Cube(1, health: 3 + tag));
        world.SaveCubeFracturePiece(Piece(1, 0));
        world.SaveEnemyInstance(Enemy(1, 10 + tag));
        world.SaveItemInstance(Item(1));
        if (tag == 1)
        {
            world.SaveItemInstance(Item(5));
        }

        world.SaveInventorySlot(new PersistedInventorySlot(Owner, 0, 1, Created));
        world.SaveStarterKitGrant(Owner, tag);
    }

    private static void AssertFilled(SqliteWorldPersistenceRepository world, int tag)
    {
        Assert.Equal(new Vector3(tag, 2f, 3f), world.TryGetPlayerWorldState(Owner)!.Value.Position);
        Assert.Equal(3 + tag, Assert.Single(world.LoadPlacedCubes()).CurrentHealth);
        Assert.Single(world.LoadCubeFracturePieces());
        Assert.Equal(10 + tag, Assert.Single(world.LoadActiveEnemies()).Stats.CurrentHealth);
        Assert.Equal(tag == 1 ? 2 : 1, world.LoadItemInstances().Count);
        PersistedInventorySlot slot = Assert.Single(world.LoadInventorySlots());
        Assert.Equal(1L, slot.ItemInstanceId);
        PersistedPlayerInventory inventory = world.LoadPlayerInventory(Owner);
        Assert.Equal([1L], inventory.Items.Select(item => item.ItemInstanceId));
        Assert.Equal(tag, inventory.StarterKitVersion);
    }

    private static long ReadTicks(PersistenceTestDatabase database, long worldId) =>
        database.Count($"SELECT world_time_ticks FROM worlds WHERE world_id = {worldId};");

    private static PersistenceTestDatabase NewDatabaseWithTwoWorlds()
    {
        PersistenceTestDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).Save(PersistenceGroupCommitTests.Profile(Owner, experience: 0));
        using SqliteConnection connection = database.Schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO worlds (world_id, name, width, height, depth, map_source, generator_key, generator_version, created_schema_version)
            VALUES (2, 'second', 20, 8, 20, 'generated', 'default', 1, 3);
            """;
        command.ExecuteNonQuery();
        return database;
    }

    private static PersistedPlacedCube Cube(int cubeId, int health) =>
        new(new ObjectId(cubeId), Owner, new Vector3(cubeId, 1f, 2f), GeometryShapeKind.SolidCube, GeometryMaterialId.Stone,
            health, 5, Created, Created, null);

    private static PersistedCubeFracturePiece Piece(int sourceCubeId, int pieceIndex) =>
        new(new ObjectId(1000 + sourceCubeId * 10 + pieceIndex), new ObjectId(sourceCubeId), pieceIndex, Owner, new Vector3(sourceCubeId, 1f, 2f),
            new Vector3(sourceCubeId + 0.25f, 1.25f, 2.25f), 0.5f, GeometryMaterialId.Stone, Created, Created);

    private static PersistedEnemyInstance Enemy(int enemyId, int hp) =>
        new(new ObjectId(enemyId), "slime", $"Slime {enemyId}", new Vector3(enemyId, 0f, enemyId), new Vector3(1f, 0f, 0f),
            new PersistedCreatureStats(hp, 20, 3, 4, 5, 6, 2.5f, 1.25f, 2), Created, Created);

    private static PersistedItemInstance Item(long id) =>
        new(id, "sword", $"Sword {id}", "sword-visual", Owner, null, "Inventory", 1, Created, Created);
}
