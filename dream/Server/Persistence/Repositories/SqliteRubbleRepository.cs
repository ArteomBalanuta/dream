using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;

namespace Dream.Server.Persistence.Repositories;

// Skeleton from the wave A contract commit; A3 moves the SQL in.
public sealed partial class SqliteRubbleRepository(IWorldStore store)
    : SqliteRepositoryBase(store), IRubbleRepository
{
    public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() =>
        throw new NotImplementedException("A3");

    public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece) =>
        throw new NotImplementedException("A3");

    public void DeleteCubeFracturePieces(int sourceCubeId) =>
        throw new NotImplementedException("A3");
}
