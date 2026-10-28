using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;

namespace Dream.Server.Persistence.Repositories;

// Skeleton from the wave A contract commit; A3 moves the SQL in.
public sealed partial class SqlitePlayerWorldStateRepository(IWorldStore store)
    : SqliteRepositoryBase(store), IPlayerWorldStateRepository
{
    public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName) =>
        throw new NotImplementedException("A3");

    public void SavePlayerWorldState(PersistedPlayerWorldState state) =>
        throw new NotImplementedException("A3");
}
