namespace Dream.Simulation.Inventory;

// A player's inventory as the owner sees it. Slots is sparse: one entry per item on the bag board (its
// SlotIndex is the anchor, 0..63) followed by the held-back items (200..215), in ascending slot order;
// an empty cell has no entry. Worn items are not in Slots: EquippedSlot is the main hand (always
// present, Item null when empty) and Equipment lists the other worn slots (101..111) that hold an item.
// Gold is the player's wallet (0 when none). The wire form is compact and written by PlayerInventorySnapshotJsonConverter.
public readonly record struct PlayerInventorySnapshot(
    string PlayerName,
    int Columns,
    int Rows,
    IReadOnlyList<InventorySlotSnapshot> Slots,
    InventorySlotSnapshot? EquippedSlot = null,
    IReadOnlyList<InventorySlotSnapshot>? Equipment = null,
    long Gold = 0)
{
    public static PlayerInventorySnapshot Empty(string playerName)
        => new(
            playerName,
            InventoryConstants.GridColumns,
            InventoryConstants.GridRows,
            [],
            EquippedSlot: new InventorySlotSnapshot(InventoryConstants.EquippedSlotIndex, Item: null));
}
