namespace Dream.Simulation.Inventory;

public readonly record struct PlayerInventorySnapshot(
    string PlayerName,
    int Columns,
    int Rows,
    IReadOnlyList<InventorySlotSnapshot> Slots,
    InventorySlotSnapshot? EquippedSlot = null,
    IReadOnlyList<InventorySlotSnapshot>? Equipment = null)
{
    public static PlayerInventorySnapshot Empty(string playerName)
    {
        InventorySlotSnapshot[] slots = Enumerable.Range(0, InventoryConstants.BackpackSlotCount)
            .Select(index => new InventorySlotSnapshot(index, Item: null))
            .ToArray();
        return new PlayerInventorySnapshot(
            playerName,
            InventoryConstants.GridColumns,
            InventoryConstants.GridRows,
            slots,
            EquippedSlot: new InventorySlotSnapshot(InventoryConstants.EquippedSlotIndex, Item: null));
    }
}
