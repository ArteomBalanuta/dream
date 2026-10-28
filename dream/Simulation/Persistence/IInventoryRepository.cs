namespace Dream.Simulation.Persistence;

public interface IInventoryRepository
{
    IReadOnlyList<PersistedInventorySlot> LoadInventorySlots();

    // One player's inventory slots, the item instances they reference and the starter kit version
    // the player has received, read in one consistent snapshot. The name matches the slot rows
    // exactly (trimmed), like the profile and world state. The runtime loads inventories per player
    // (join prefetch) instead of all of them at startup.
    PersistedPlayerInventory LoadPlayerInventory(string playerName);

    void SaveInventorySlot(PersistedInventorySlot slot);

    void DeleteInventorySlots(string playerName);

    // Records that the player has received the starter kit up to kitVersion (see
    // ToolCatalog.StarterKit). Never lowers a recorded version; DeleteInventorySlots keeps it.
    void SaveStarterKitGrant(string playerName, int kitVersion);
}
