using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;

namespace Dream.Server.Persistence.Repositories;

// Skeleton from the wave A contract commit; A3 moves the SQL in.
public sealed partial class SqliteItemRepository(IWorldStore store)
    : SqliteRepositoryBase(store), IItemRepository
{
    public IReadOnlyList<PersistedItemInstance> LoadItemInstances() =>
        throw new NotImplementedException("A3");

    public void SaveItemInstance(PersistedItemInstance item) =>
        throw new NotImplementedException("A3");

    public void DeleteItemInstance(long itemInstanceId) =>
        throw new NotImplementedException("A3");

    public long GetMaxItemInstanceId() =>
        throw new NotImplementedException("A3");
}
