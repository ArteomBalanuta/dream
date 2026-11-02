namespace Dream.Simulation.Persistence;

public interface IItemRepository
{
    IReadOnlyList<PersistedItemInstance> LoadItemInstances();

    void SaveItemInstance(PersistedItemInstance item);

    void DeleteItemInstance(long itemInstanceId);

    // The items lying on the ground: state 'Ground' and referenced by no inventory slot. Default
    // implementation (a fake without ground items) returns none.
    IReadOnlyList<PersistedItemInstance> LoadGroundItems() => [];

    // Deletes an item row, but only while it is still in state 'Ground': a stale despawn can never
    // erase an item that has since entered an inventory (the cascade would take its slot row). The
    // default implementation does nothing.
    void DeleteGroundItem(long itemInstanceId)
    {
    }
}
