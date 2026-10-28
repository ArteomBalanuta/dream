using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;

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

    public SqliteWorldPersistenceRepository(SqliteSchemaBootstrapper schema)
    {
        SqliteWorldStore store = new(schema);
        _playerWorldState = new SqlitePlayerWorldStateRepository(store);
        _placedObjects = new SqlitePlacedObjectRepository(store);
        _rubble = new SqliteRubbleRepository(store);
        _enemies = new SqliteEnemyRepository(store);
        _items = new SqliteItemRepository(store);
        _inventory = new SqliteInventoryRepository(store);
    }

    public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName) => _playerWorldState.TryGetPlayerWorldState(playerName);

    public void SavePlayerWorldState(PersistedPlayerWorldState state) => _playerWorldState.SavePlayerWorldState(state);

    public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() => _placedObjects.LoadPlacedCubes();

    public void SavePlacedCube(PersistedPlacedCube cube) => _placedObjects.SavePlacedCube(cube);

    public void DeletePlacedCube(int cubeId) => _placedObjects.DeletePlacedCube(cubeId);

    public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() => _rubble.LoadCubeFracturePieces();

    public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece) => _rubble.SaveCubeFracturePiece(piece);

    public void DeleteCubeFracturePieces(int sourceCubeId) => _rubble.DeleteCubeFracturePieces(sourceCubeId);

    public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() => _enemies.LoadActiveEnemies();

    public void SaveEnemyInstance(PersistedEnemyInstance enemy) => _enemies.SaveEnemyInstance(enemy);

    public void DeleteEnemyInstance(int enemyId) => _enemies.DeleteEnemyInstance(enemyId);

    public void ReassignEnemyId(int oldEnemyId, int newEnemyId) => _enemies.ReassignEnemyId(oldEnemyId, newEnemyId);

    public IReadOnlyList<PersistedItemInstance> LoadItemInstances() => _items.LoadItemInstances();

    public void SaveItemInstance(PersistedItemInstance item) => _items.SaveItemInstance(item);

    public void DeleteItemInstance(long itemInstanceId) => _items.DeleteItemInstance(itemInstanceId);

    public long GetMaxItemInstanceId() => _items.GetMaxItemInstanceId();

    public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots() => _inventory.LoadInventorySlots();

    public PersistedPlayerInventory LoadPlayerInventory(string playerName) => _inventory.LoadPlayerInventory(playerName);

    public void SaveInventorySlot(PersistedInventorySlot slot) => _inventory.SaveInventorySlot(slot);

    public void DeleteInventorySlots(string playerName) => _inventory.DeleteInventorySlots(playerName);

    public void Dispose()
    {
    }
}
