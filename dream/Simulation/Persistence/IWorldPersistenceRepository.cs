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

    void SaveInventorySlot(PersistedInventorySlot slot);

    void DeleteInventorySlots(string playerName);
}
