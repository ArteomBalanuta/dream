namespace Dream.Simulation.Inventory;

// The slot rows of a player_inventory_slots table, by slot_index:
//   0..63     bag anchors: the top-left cell (row * 8 + col) of an item on the 8x8 bag board
//   100..111  worn: one row per equipment slot (EquipmentSlots)
//   200..215  held back: items that did not fit the board on load, waiting for room (never used for loot)
//   300 up    parked: overflow of the held-back rows (hand-edited data only), kept and inert
// Any other index is an unknown row: kept and inert.
public static class InventoryConstants
{
    public const int GridColumns = BagGrid.Columns;
    public const int GridRows = BagGrid.Rows;

    // The number of bag cells, which is also the number of anchor values (0..63).
    public const int BackpackSlotCount = GridColumns * GridRows;

    // The most gold a wallet holds; a credit that would pass it is refused.
    public const long MaxGold = 2_000_000_000L;

    public const int EquippedSlotIndex = 100;

    public const int HeldBackFirstIndex = 200;
    public const int HeldBackCount = 16;
    public const int HeldBackLastIndex = HeldBackFirstIndex + HeldBackCount - 1;

    // The first index overflow rows are parked at when all held-back rows are taken.
    public const int ParkedFirstIndex = 300;

    public static bool IsBagAnchor(int slotIndex) => (uint)slotIndex < (uint)BackpackSlotCount;

    public static bool IsHeldBack(int slotIndex) => slotIndex >= HeldBackFirstIndex && slotIndex <= HeldBackLastIndex;

    // A row the inventory shows: a bag anchor, a worn slot or a held-back row.
    public static bool IsKnownRow(int slotIndex)
        => IsBagAnchor(slotIndex) || EquipmentSlots.TryFromSlotIndex(slotIndex, out _) || IsHeldBack(slotIndex);
}
