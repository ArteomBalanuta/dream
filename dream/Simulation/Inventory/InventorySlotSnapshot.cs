namespace Dream.Simulation.Inventory;

public readonly record struct InventorySlotSnapshot(
    int SlotIndex,
    InventoryItemSnapshot? Item,
    bool IsEquipped = false);
