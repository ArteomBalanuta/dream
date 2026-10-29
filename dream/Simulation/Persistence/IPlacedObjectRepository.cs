using Dream.Simulation.World;

namespace Dream.Simulation.Persistence;

public interface IPlacedObjectRepository
{
    IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes();

    void SavePlacedCube(PersistedPlacedCube cube);

    void DeletePlacedCube(ObjectId objectId);
}
