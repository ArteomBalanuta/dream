using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;

namespace Dream.Server.Persistence;

// The world repository as one object: forwards each call to the per-aggregate repository that owns
// its table. All six share one store, so every call opens one session and enlists in an active
// SqliteWriteBatch as before.
public sealed class SqliteWorldPersistenceRepository : IWorldPersistenceRepository, IDisposable
{
    // Kept for the query-plan test.
    internal const string LoadPlayerInventorySql = SqliteInventoryRepository.LoadPlayerInventorySql;

    private readonly SqlitePlayerWorldStateRepository _playerWorldState;
    private readonly SqlitePlacedObjectRepository _placedObjects;
    private readonly SqliteRubbleRepository _rubble;
    private readonly SqliteEnemyRepository _enemies;
    private readonly SqliteItemRepository _items;
    private readonly SqliteInventoryRepository _inventory;
    private readonly SqliteObjectIdCounterRepository _objectIdCounter;
    private readonly SqliteTerrainOverrideRepository _terrainOverrides;

    // Bound to one world: every statement of the six repositories is scoped to its world_id.
    public SqliteWorldPersistenceRepository(SqliteSchemaBootstrapper schema, WorldId worldId)
    {
        SqliteWorldStore store = new(schema);
        _playerWorldState = new SqlitePlayerWorldStateRepository(store, worldId);
        _placedObjects = new SqlitePlacedObjectRepository(store, worldId);
        _rubble = new SqliteRubbleRepository(store, worldId);
        _enemies = new SqliteEnemyRepository(store, worldId);
        _items = new SqliteItemRepository(store, worldId);
        _inventory = new SqliteInventoryRepository(store, worldId);
        _objectIdCounter = new SqliteObjectIdCounterRepository(store, worldId);
        _terrainOverrides = new SqliteTerrainOverrideRepository(store, worldId);
        TerrainChunks = new SqliteTerrainChunkRepository(store, worldId);
    }

    // World 1: the default world.
    public SqliteWorldPersistenceRepository(SqliteSchemaBootstrapper schema)
        : this(schema, WorldId.Default)
    {
    }

    public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName) => _playerWorldState.TryGetPlayerWorldState(playerName);

    public void SavePlayerWorldState(PersistedPlayerWorldState state) => _playerWorldState.SavePlayerWorldState(state);

    public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() => _placedObjects.LoadPlacedCubes();

    public void SavePlacedCube(PersistedPlacedCube cube) => _placedObjects.SavePlacedCube(cube);

    public void DeletePlacedCube(ObjectId objectId) => _placedObjects.DeletePlacedCube(objectId);

    public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() => _rubble.LoadCubeFracturePieces();

    public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece) => _rubble.SaveCubeFracturePiece(piece);

    public void DeleteCubeFracturePieces(ObjectId originObjectId) => _rubble.DeleteCubeFracturePieces(originObjectId);

    public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() => _enemies.LoadActiveEnemies();

    public void SaveEnemyInstance(PersistedEnemyInstance enemy) => _enemies.SaveEnemyInstance(enemy);

    public void DeleteEnemyInstance(ObjectId objectId) => _enemies.DeleteEnemyInstance(objectId);


    public IReadOnlyList<PersistedItemInstance> LoadItemInstances() => _items.LoadItemInstances();

    public void SaveItemInstance(PersistedItemInstance item) => _items.SaveItemInstance(item);

    public void DeleteItemInstance(long itemInstanceId) => _items.DeleteItemInstance(itemInstanceId);

    public IReadOnlyList<PersistedItemInstance> LoadGroundItems() => _items.LoadGroundItems();

    public void DeleteGroundItem(long itemInstanceId) => _items.DeleteGroundItem(itemInstanceId);


    public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots() => _inventory.LoadInventorySlots();

    public PersistedPlayerInventory LoadPlayerInventory(string playerName) => _inventory.LoadPlayerInventory(playerName);

    public void SaveInventorySlot(PersistedInventorySlot slot) => _inventory.SaveInventorySlot(slot);

    public void DeleteInventorySlots(string playerName) => _inventory.DeleteInventorySlots(playerName);

    public void DeleteInventorySlot(string playerName, int slotIndex, long expectedItemInstanceId) => _inventory.DeleteInventorySlot(playerName, slotIndex, expectedItemInstanceId);

    public void DestroyInventoryItem(string playerName, long itemInstanceId) => _inventory.DestroyInventoryItem(playerName, itemInstanceId);

    public void SaveStarterKitGrant(string playerName, int kitVersion) => _inventory.SaveStarterKitGrant(playerName, kitVersion);

    public void ApplyWalletDelta(string playerName, long delta) => _inventory.ApplyWalletDelta(playerName, delta);

    public long LoadNextObjectId() => _objectIdCounter.LoadNextObjectId();

    public IReadOnlyList<PersistedTerrainCellOverride> LoadTerrainCellOverrides() => _terrainOverrides.LoadTerrainCellOverrides();

    public void SaveTerrainCellOverride(PersistedTerrainCellOverride cellOverride) => _terrainOverrides.SaveTerrainCellOverride(cellOverride);

    // The stored-map chunks of this world: the admin and export path, not part of the write facade.
    public ITerrainChunkRepository TerrainChunks { get; }

    public void Dispose()
    {
    }
}
