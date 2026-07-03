namespace Dream.Server.Persistence;

public interface IPlayerProfileRepository
{
    PersistedPlayerProfile GetOrCreate(string playerName);

    PersistedPlayerProfile? TryGet(string playerName);

    void Save(PersistedPlayerProfile profile);
}
