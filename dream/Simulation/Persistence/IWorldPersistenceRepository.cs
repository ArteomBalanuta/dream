namespace Dream.Simulation.Persistence;

public interface IWorldPersistenceRepository
{
    PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName);

    void SavePlayerWorldState(PersistedPlayerWorldState state);

    IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes();

    void SavePlacedCube(PersistedPlacedCube cube);

    void DeletePlacedCube(int cubeId);

    IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces();

    void SaveCubeFracturePiece(PersistedCubeFracturePiece piece);

    void DeleteCubeFracturePieces(int sourceCubeId);

    IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies();

    void SaveEnemyInstance(PersistedEnemyInstance enemy);

    void DeleteEnemyInstance(int enemyId);

    // Moves an enemy's row from oldEnemyId to newEnemyId in one atomic step, so a failure leaves it
    // under exactly one of the two ids. No-op when there is no row under oldEnemyId; fails (and
    // changes nothing) when newEnemyId is already taken.
    void ReassignEnemyId(int oldEnemyId, int newEnemyId);

    IReadOnlyList<PersistedItemInstance> LoadItemInstances();

    void SaveItemInstance(PersistedItemInstance item);

    void DeleteItemInstance(long itemInstanceId);

    IReadOnlyList<PersistedInventorySlot> LoadInventorySlots();

    // One player's inventory slots and the item instances they reference, read in one consistent
    // snapshot. The name matches the slot rows exactly (trimmed), like the profile and world state.
    // The runtime loads inventories per player (join prefetch) instead of all of them at startup.
    PersistedPlayerInventory LoadPlayerInventory(string playerName);

    // The highest item instance id any persisted row uses (item or slot), or 0 when there is none.
    // New item ids start above it, so they never collide with an inventory that is not loaded.
    long GetMaxItemInstanceId();

    void SaveInventorySlot(PersistedInventorySlot slot);

    void DeleteInventorySlots(string playerName);
}
