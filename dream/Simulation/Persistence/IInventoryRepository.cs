namespace Dream.Simulation.Persistence;

public interface IInventoryRepository
{
    IReadOnlyList<PersistedInventorySlot> LoadInventorySlots();

    // One player's inventory slots and the item instances they reference, read in one consistent
    // snapshot. The name matches the slot rows exactly (trimmed), like the profile and world state.
    // The runtime loads inventories per player (join prefetch) instead of all of them at startup.
    PersistedPlayerInventory LoadPlayerInventory(string playerName);

    void SaveInventorySlot(PersistedInventorySlot slot);

    void DeleteInventorySlots(string playerName);
}
