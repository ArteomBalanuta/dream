namespace Dream.Simulation.Persistence;

public interface IItemRepository
{
    IReadOnlyList<PersistedItemInstance> LoadItemInstances();

    void SaveItemInstance(PersistedItemInstance item);

    void DeleteItemInstance(long itemInstanceId);

    // Removed by the wave C glue. Item ids now come from the shared object id allocator, so the
    // runtime no longer calls this.
    // The highest item instance id any persisted row uses (item or slot), or 0 when there is none.
    // New item ids start above it, so they never collide with an inventory that is not loaded.
    long GetMaxItemInstanceId();
}
