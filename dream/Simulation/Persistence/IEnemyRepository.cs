using Dream.Simulation.World;

namespace Dream.Simulation.Persistence;

public interface IEnemyRepository
{
    IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies();

    void SaveEnemyInstance(PersistedEnemyInstance enemy);

    void DeleteEnemyInstance(ObjectId objectId);

    // Removed by the wave C glue.
    // Moves an enemy's row from oldEnemyId to newEnemyId in one atomic step, so a failure leaves it
    // under exactly one of the two ids. No-op when there is no row under oldEnemyId; fails (and
    // changes nothing) when newEnemyId is already taken.
    void ReassignEnemyId(int oldEnemyId, int newEnemyId);
}
