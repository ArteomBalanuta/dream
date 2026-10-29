using Dream.Simulation.World;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World.Geometry;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Server.Persistence.Repositories;

// Wave A (A3): the world repository split into one repository per aggregate. Each part round-trips
// its rows, enlists in an active SqliteWriteBatch (one commit for the group), and reads back the
// same rows as the SqliteWorldPersistenceRepository facade.
public sealed class WorldAggregateRepositoryTests
{
    private const string Owner = "alice";
    private static readonly DateTime Created = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public void PlayerWorldState_RoundTrips_AndStampsTheStatesTime()
    {
        using PersistenceTestDatabase database = NewDatabaseWithOwner();
        SqlitePlayerWorldStateRepository repository = new(new SqliteWorldStore(database.Schema));

        Assert.Null(repository.TryGetPlayerWorldState(Owner));

        repository.SavePlayerWorldState(WorldState(new Vector3(1f, 2f, 3f), hp: 40));
        PersistedPlayerWorldState saved = repository.TryGetPlayerWorldState($"  {Owner} ")!.Value;
        Assert.Equal(Owner, saved.PlayerName);
        Assert.Equal(new Vector3(1f, 2f, 3f), saved.Position);
        Assert.Equal(new Vector3(0f, 0f, -1f), saved.Facing);
        Assert.Equal(40, saved.CurrentHealth);
        Assert.Equal(7, saved.CurrentEnergy);
        // The row carries state.UpdatedUtc (Created), the state's capture time, not the write time.
        Assert.Equal(Created, saved.UpdatedUtc);

        repository.SavePlayerWorldState(WorldState(new Vector3(4f, 5f, 6f), hp: 12));
        PersistedPlayerWorldState updated = repository.TryGetPlayerWorldState(Owner)!.Value;
        Assert.Equal(new Vector3(4f, 5f, 6f), updated.Position);
        Assert.Equal(12, updated.CurrentHealth);
        Assert.Equal(1L, database.Count("SELECT COUNT(*) FROM player_world_state;"));

        // A default UpdatedUtc is stamped with the write time.
        DateTime before = DateTime.UtcNow;
        repository.SavePlayerWorldState(WorldState(new Vector3(7f, 8f, 9f), hp: 3) with { UpdatedUtc = default });
        DateTime after = DateTime.UtcNow;
        Assert.InRange(repository.TryGetPlayerWorldState(Owner)!.Value.UpdatedUtc, before, after);
    }

    [Fact]
    public void PlacedObjects_CreateReadUpdateDelete_KeepTheCreatedTimeAndANullCenter()
    {
        using PersistenceTestDatabase database = NewDatabaseWithOwner();
        SqlitePlacedObjectRepository repository = new(new SqliteWorldStore(database.Schema));

        repository.SavePlacedCube(Cube(1, health: 5, center: null));
        repository.SavePlacedCube(Cube(2, health: 5, center: new Vector3(2.5f, 1.5f, 3.5f)));
        repository.SavePlacedCube(Cube(1, health: 3, center: null));

        IReadOnlyList<PersistedPlacedCube> cubes = repository.LoadPlacedCubes();
        Assert.Equal([1, 2], cubes.Select(cube => (int)cube.ObjectId.Value));
        Assert.Equal(3, cubes[0].CurrentHealth);
        Assert.Null(cubes[0].CenterPosition);
        Assert.Equal(new Vector3(2.5f, 1.5f, 3.5f), cubes[1].CenterPosition);
        Assert.Equal(GeometryShapeKind.SolidCube, cubes[1].ShapeKind);
        Assert.Equal(GeometryMaterialId.Stone, cubes[1].MaterialId);
        Assert.Equal(Created, cubes[0].CreatedUtc);

        repository.DeletePlacedCube(new ObjectId(1));
        Assert.Equal([2], repository.LoadPlacedCubes().Select(cube => (int)cube.ObjectId.Value));
    }

    [Fact]
    public void Rubble_CreateReadUpdateDelete_BySourceCube()
    {
        using PersistenceTestDatabase database = NewDatabaseWithOwner();
        SqliteRubbleRepository repository = new(new SqliteWorldStore(database.Schema));

        repository.SaveCubeFracturePiece(Piece(sourceCubeId: 9, pieceIndex: 1, side: 0.5f));
        repository.SaveCubeFracturePiece(Piece(sourceCubeId: 9, pieceIndex: 0, side: 0.5f));
        repository.SaveCubeFracturePiece(Piece(sourceCubeId: 10, pieceIndex: 0, side: 0.25f));
        repository.SaveCubeFracturePiece(Piece(sourceCubeId: 9, pieceIndex: 1, side: 0.125f));

        IReadOnlyList<PersistedCubeFracturePiece> pieces = repository.LoadCubeFracturePieces();
        Assert.Equal([(9, 0), (9, 1), (10, 0)], pieces.Select(piece => ((int)piece.OriginObjectId.Value, piece.PieceIndex)));
        Assert.Equal(0.125f, pieces[1].SideLength);
        Assert.Equal(new Vector3(9f, 1f, 2f), pieces[1].ParentCell);
        Assert.Equal(new Vector3(9.25f, 1.25f, 2.25f), pieces[1].Center);
        Assert.Equal(Created, pieces[1].CreatedUtc);

        repository.DeleteCubeFracturePieces(new ObjectId(9));
        Assert.Equal([10], repository.LoadCubeFracturePieces().Select(piece => (int)piece.OriginObjectId.Value));
    }

    [Fact]
    public void Enemies_CreateReadUpdateReassignDelete()
    {
        using PersistenceTestDatabase database = NewDatabaseWithOwner();
        SqliteEnemyRepository repository = new(new SqliteWorldStore(database.Schema));

        repository.SaveEnemyInstance(Enemy(3, hp: 20));
        repository.SaveEnemyInstance(Enemy(4, hp: 20));
        repository.SaveEnemyInstance(Enemy(3, hp: 11));

        IReadOnlyList<PersistedEnemyInstance> enemies = repository.LoadActiveEnemies();
        Assert.Equal([3, 4], enemies.Select(enemy => (int)enemy.ObjectId.Value));
        Assert.Equal(11, enemies[0].Stats.CurrentHealth);
        Assert.Equal(2.5f, enemies[0].Stats.MovementSpeed);
        Assert.Equal(1.25f, enemies[0].Stats.AttackSpeed);
        Assert.Equal(new Vector3(3f, 0f, 3f), enemies[0].Position);
        Assert.Equal(Created, enemies[0].CreatedUtc);

        repository.DeleteEnemyInstance(new ObjectId(4));
        Assert.Equal([3], repository.LoadActiveEnemies().Select(enemy => (int)enemy.ObjectId.Value));
    }

    [Fact]
    public void Items_CreateReadUpdateDelete()
    {
        using PersistenceTestDatabase database = NewDatabaseWithOwner();
        SqliteItemRepository items = new(new SqliteWorldStore(database.Schema));

        items.SaveItemInstance(Item(5, owner: null, position: new Vector3(1f, 2f, 3f)));
        items.SaveItemInstance(Item(6, owner: Owner, position: null));
        items.SaveItemInstance(Item(5, owner: null, position: new Vector3(7f, 8f, 9f)));

        IReadOnlyList<PersistedItemInstance> loaded = items.LoadItemInstances();
        Assert.Equal([5L, 6L], loaded.Select(item => item.ItemInstanceId));
        Assert.Equal(new Vector3(7f, 8f, 9f), loaded[0].WorldPosition);
        Assert.Null(loaded[0].OwnerPlayerName);
        Assert.Null(loaded[1].WorldPosition);
        Assert.Equal(Owner, loaded[1].OwnerPlayerName);
        Assert.Equal(Created, loaded[1].CreatedUtc);

        items.DeleteItemInstance(6);
        Assert.Equal([5L], items.LoadItemInstances().Select(item => item.ItemInstanceId));
    }

    [Fact]
    public void Inventory_CreateReadUpdateDelete_AndLoadsOnePlayersItemsOnce()
    {
        using PersistenceTestDatabase database = NewDatabaseWithOwner();
        SqliteWorldStore store = new(database.Schema);
        SqliteItemRepository items = new(store);
        SqliteInventoryRepository inventory = new(store);
        items.SaveItemInstance(Item(1, owner: Owner, position: null));
        items.SaveItemInstance(Item(2, owner: Owner, position: null));

        inventory.SaveInventorySlot(new PersistedInventorySlot(Owner, 2, 1, Created));
        inventory.SaveInventorySlot(new PersistedInventorySlot($" {Owner} ", 0, 2, Created));
        // The same item in a second slot.
        inventory.SaveInventorySlot(new PersistedInventorySlot(Owner, 1, 2, Created));
        inventory.SaveInventorySlot(new PersistedInventorySlot(Owner, 2, 2, Created));

        IReadOnlyList<PersistedInventorySlot> slots = inventory.LoadInventorySlots();
        Assert.Equal([(0, 2L), (1, 2L), (2, 2L)], slots.Select(slot => (slot.SlotIndex, slot.ItemInstanceId)));
        Assert.All(slots, slot => Assert.Equal(Owner, slot.PlayerName));

        PersistedPlayerInventory loaded = inventory.LoadPlayerInventory($"{Owner} ");
        Assert.Equal(slots, loaded.Slots);
        Assert.Equal([2L], loaded.Items.Select(item => item.ItemInstanceId));
        Assert.Empty(inventory.LoadPlayerInventory("bob").Slots);

        inventory.DeleteInventorySlots($" {Owner}");
        Assert.Empty(inventory.LoadInventorySlots());
    }

    public static TheoryData<string> Aggregates() =>
        ["PlayerWorldState", "PlacedObjects", "Rubble", "Enemies", "Items", "Inventory"];

    [Theory]
    [MemberData(nameof(Aggregates))]
    public void WritesInsideABatch_EnlistAndCommitOnce(string aggregate)
    {
        using PersistenceTestDatabase database = NewDatabaseWithOwner();
        SqliteWorldStore store = new(database.Schema);
        (Action writes, string countSql, long expected) = aggregate switch
        {
            "PlayerWorldState" => ((Action)(() =>
                new SqlitePlayerWorldStateRepository(store).SavePlayerWorldState(WorldState(Vector3.One, hp: 9))),
                "SELECT COUNT(*) FROM player_world_state;", 1L),
            "PlacedObjects" => (() =>
            {
                SqlitePlacedObjectRepository cubes = new(store);
                cubes.SavePlacedCube(Cube(1, 5, null));
                cubes.SavePlacedCube(Cube(2, 5, null));
                cubes.SavePlacedCube(Cube(3, 5, null));
                cubes.DeletePlacedCube(new ObjectId(2));
            }, "SELECT COUNT(*) FROM placed_cube_instances;", 2L),
            "Rubble" => (() =>
            {
                SqliteRubbleRepository rubble = new(store);
                rubble.SaveCubeFracturePiece(Piece(1, 0, 0.5f));
                rubble.SaveCubeFracturePiece(Piece(1, 1, 0.5f));
                rubble.SaveCubeFracturePiece(Piece(2, 0, 0.5f));
                rubble.DeleteCubeFracturePieces(new ObjectId(2));
            }, "SELECT COUNT(*) FROM cube_fracture_pieces;", 2L),
            "Enemies" => (() =>
            {
                SqliteEnemyRepository enemies = new(store);
                enemies.SaveEnemyInstance(Enemy(1, 10));
                enemies.SaveEnemyInstance(Enemy(2, 10));
                enemies.DeleteEnemyInstance(new ObjectId(2));
            }, "SELECT COUNT(*) FROM enemy_instances WHERE object_id = 1;", 1L),
            "Items" => (() =>
            {
                SqliteItemRepository items = new(store);
                items.SaveItemInstance(Item(1, Owner, null));
                items.SaveItemInstance(Item(2, Owner, null));
                items.SaveItemInstance(Item(3, Owner, null));
                items.DeleteItemInstance(3);
            }, "SELECT COUNT(*) FROM item_instances;", 2L),
            "Inventory" => (() =>
            {
                SqliteInventoryRepository inventory = new(store);
                inventory.SaveInventorySlot(new PersistedInventorySlot(Owner, 0, 1, Created));
                inventory.SaveInventorySlot(new PersistedInventorySlot(Owner, 1, 2, Created));
                inventory.DeleteInventorySlots("bob");
            }, "SELECT COUNT(*) FROM player_inventory_slots;", 2L),
            _ => throw new ArgumentOutOfRangeException(nameof(aggregate)),
        };
        if (aggregate == "Inventory")
        {
            SqliteItemRepository items = new(store);
            items.SaveItemInstance(Item(1, Owner, null));
            items.SaveItemInstance(Item(2, Owner, null));
        }

        using SqliteWriteBatch batch = new();
        batch.Activate();
        try
        {
            batch.BeginWrite();
            writes();
            batch.CompleteWrite();
            // Every write ran on the batch's transaction: another connection sees none of them yet.
            Assert.Equal(0L, database.Count(countSql));
            batch.Commit();
        }
        finally
        {
            SqliteWriteBatch.Deactivate();
        }

        Assert.Equal(1L, batch.CommittedTransactions);
        Assert.Equal(expected, database.Count(countSql));
    }

    [Fact]
    public void TheFacadeAndTheParts_ReadBackIdenticalRows()
    {
        using PersistenceTestDatabase database = NewDatabaseWithOwner();
        using SqliteWorldPersistenceRepository facade = new(database.Schema);
        facade.SavePlayerWorldState(WorldState(new Vector3(1f, 2f, 3f), hp: 50));
        facade.SavePlacedCube(Cube(1, health: 4, center: new Vector3(1.5f, 0.5f, 1.5f)));
        facade.SavePlacedCube(Cube(2, health: 4, center: null));
        facade.SaveCubeFracturePiece(Piece(1, 0, 0.5f));
        facade.SaveEnemyInstance(Enemy(7, hp: 15));
        facade.SaveItemInstance(Item(3, owner: Owner, position: null));
        facade.SaveItemInstance(Item(4, owner: null, position: new Vector3(4f, 0f, 4f)));
        facade.SaveInventorySlot(new PersistedInventorySlot(Owner, 0, 3, Created));

        SqliteWorldStore store = new(database.Schema);
        SqlitePlayerWorldStateRepository worldState = new(store);
        SqlitePlacedObjectRepository cubes = new(store);
        SqliteRubbleRepository rubble = new(store);
        SqliteEnemyRepository enemies = new(store);
        SqliteItemRepository items = new(store);
        SqliteInventoryRepository inventory = new(store);

        Assert.Equal(facade.TryGetPlayerWorldState(Owner), worldState.TryGetPlayerWorldState(Owner));
        Assert.Equal(facade.LoadPlacedCubes(), cubes.LoadPlacedCubes());
        Assert.Equal(facade.LoadCubeFracturePieces(), rubble.LoadCubeFracturePieces());
        Assert.Equal(facade.LoadActiveEnemies(), enemies.LoadActiveEnemies());
        Assert.Equal(facade.LoadItemInstances(), items.LoadItemInstances());
        Assert.Equal(facade.LoadInventorySlots(), inventory.LoadInventorySlots());
        PersistedPlayerInventory fromFacade = facade.LoadPlayerInventory(Owner);
        PersistedPlayerInventory fromPart = inventory.LoadPlayerInventory(Owner);
        Assert.Equal(fromFacade.Slots, fromPart.Slots);
        Assert.Equal(fromFacade.Items, fromPart.Items);
        Assert.Single(fromPart.Items);
        Assert.Equal(SqliteInventoryRepository.LoadPlayerInventorySql, SqliteWorldPersistenceRepository.LoadPlayerInventorySql);
    }

    private static PersistenceTestDatabase NewDatabaseWithOwner()
    {
        PersistenceTestDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).Save(PersistenceGroupCommitTests.Profile(Owner, experience: 0));
        return database;
    }

    private static PersistedPlayerWorldState WorldState(Vector3 position, int hp) =>
        new(Owner, position, new Vector3(0f, 0f, -1f), hp, 7, Created);

    private static PersistedPlacedCube Cube(int cubeId, int health, Vector3? center) =>
        new(new ObjectId(cubeId), Owner, new Vector3(cubeId, 1f, 2f), GeometryShapeKind.SolidCube, GeometryMaterialId.Stone,
            health, 5, Created, Created, center);

    private static PersistedCubeFracturePiece Piece(int sourceCubeId, int pieceIndex, float side) =>
        new(ObjectId.None, new ObjectId(sourceCubeId), pieceIndex, Owner, new Vector3(sourceCubeId, 1f, 2f),
            new Vector3(sourceCubeId + 0.25f, 1.25f, 2.25f), side, GeometryMaterialId.Stone, Created, Created);

    private static PersistedEnemyInstance Enemy(int enemyId, int hp) =>
        new(new ObjectId(enemyId), "slime", $"Slime {enemyId}", new Vector3(enemyId, 0f, enemyId), new Vector3(1f, 0f, 0f),
            new PersistedCreatureStats(hp, 20, 3, 4, 5, 6, 2.5f, 1.25f, 2), Created, Created);

    private static PersistedItemInstance Item(long id, string? owner, Vector3? position) =>
        new(id, "sword", $"Sword {id}", "sword-visual", owner, position, owner == null ? "World" : "Inventory", 1, Created, Created);
}
