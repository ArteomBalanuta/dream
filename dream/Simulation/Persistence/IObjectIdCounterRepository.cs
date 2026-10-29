namespace Dream.Simulation.Persistence;

public interface IObjectIdCounterRepository
{
    // The next ObjectId to issue: never below any persisted object id, and never lowered by deletes.
    long LoadNextObjectId();
}
