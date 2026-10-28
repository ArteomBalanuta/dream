using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;

namespace Dream.Server.Persistence.Repositories;

// Skeleton from the wave A contract commit; A3 moves the SQL in.
public sealed partial class SqliteEnemyRepository(IWorldStore store)
    : SqliteRepositoryBase(store), IEnemyRepository
{
    public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() =>
        throw new NotImplementedException("A3");

    public void SaveEnemyInstance(PersistedEnemyInstance enemy) =>
        throw new NotImplementedException("A3");

    public void DeleteEnemyInstance(int enemyId) =>
        throw new NotImplementedException("A3");

    public void ReassignEnemyId(int oldEnemyId, int newEnemyId) =>
        throw new NotImplementedException("A3");
}
