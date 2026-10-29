using Dream.Simulation.World;

namespace Dream.Simulation.Persistence;

public interface IEnemyRepository
{
    IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies();

    void SaveEnemyInstance(PersistedEnemyInstance enemy);

    void DeleteEnemyInstance(ObjectId objectId);
}
