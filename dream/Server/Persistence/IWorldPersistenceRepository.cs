namespace Dream.Server.Persistence;

public interface IWorldPersistenceRepository
{
    PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName);

    void SavePlayerWorldState(PersistedPlayerWorldState state);

    IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes();

    void SavePlacedCube(PersistedPlacedCube cube);

    void DeletePlacedCube(int cubeId);

    IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies();

    void SaveEnemyInstance(PersistedEnemyInstance enemy);

    void DeleteEnemyInstance(int enemyId);

    IReadOnlyList<PersistedItemInstance> LoadItemInstances();

    void SaveItemInstance(PersistedItemInstance item);

    void DeleteItemInstance(long itemInstanceId);

    IReadOnlyList<PersistedInventorySlot> LoadInventorySlots();

    void SaveInventorySlot(PersistedInventorySlot slot);

    void DeleteInventorySlots(string playerName);
}
