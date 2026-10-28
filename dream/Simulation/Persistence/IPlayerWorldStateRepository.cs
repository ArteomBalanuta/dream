namespace Dream.Simulation.Persistence;

public interface IPlayerWorldStateRepository
{
    PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName);

    void SavePlayerWorldState(PersistedPlayerWorldState state);
}
