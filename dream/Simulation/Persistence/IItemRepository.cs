namespace Dream.Simulation.Persistence;

public interface IItemRepository
{
    IReadOnlyList<PersistedItemInstance> LoadItemInstances();

    void SaveItemInstance(PersistedItemInstance item);

    void DeleteItemInstance(long itemInstanceId);
}
