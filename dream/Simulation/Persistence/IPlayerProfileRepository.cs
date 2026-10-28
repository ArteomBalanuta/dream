namespace Dream.Simulation.Persistence;

public interface IPlayerProfileRepository
{
    PersistedPlayerProfile GetOrCreate(string playerName);

    PersistedPlayerProfile? TryGet(string playerName);

    void Save(PersistedPlayerProfile profile);
}
