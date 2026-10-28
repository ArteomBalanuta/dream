using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;

namespace Dream.Server.Persistence.Repositories;

// Skeleton from the wave A contract commit; A3 moves the SQL in.
public sealed partial class SqlitePlacedObjectRepository(IWorldStore store)
    : SqliteRepositoryBase(store), IPlacedObjectRepository
{
    public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() =>
        throw new NotImplementedException("A3");

    public void SavePlacedCube(PersistedPlacedCube cube) =>
        throw new NotImplementedException("A3");

    public void DeletePlacedCube(int cubeId) =>
        throw new NotImplementedException("A3");
}
