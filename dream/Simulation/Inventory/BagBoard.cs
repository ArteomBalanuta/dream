namespace Dream.Simulation.Inventory;

// A mutable 8x8 bag board for the server side: which anchor covers which cell, each anchor's
// footprint and the occupancy mask, in the shapes BagGrid takes. Built from a player's rows by the
// inventory service and by the offline item command; placing is first come, first kept, so a row that
// runs off the board or overlaps an earlier one is left out (TryPlace returns false) and the caller
// decides what to do with it. Not thread safe; one board belongs to one operation.
internal sealed class BagBoard
{
    public BagBoard()
    {
        Owners = new sbyte[BagGrid.CellCount];
        Footprints = new ItemFootprint[BagGrid.CellCount];
        Clear();
    }

    // One entry per cell: the anchor of the item covering it, or -1.
    public sbyte[] Owners { get; }

    // One entry per anchor (only anchors of placed items are meaningful).
    public ItemFootprint[] Footprints { get; }

    // Bit (row * 8 + col) per covered cell.
    public ulong Occupied { get; private set; }

    public int ItemCount { get; private set; }

    public void Clear()
    {
        Array.Fill(Owners, (sbyte)-1);
        Array.Clear(Footprints);
        Occupied = 0;
        ItemCount = 0;
    }

    public bool IsAnchor(int cell) => (uint)cell < BagGrid.CellCount && Owners[cell] == cell;

    // Places an item with its top-left cell at anchor. False, changing nothing, when the footprint is
    // malformed, runs past the board, or covers a cell that is taken.
    public bool TryPlace(int anchor, ItemFootprint footprint)
    {
        if (!BagGrid.TryMask(anchor, footprint, out ulong mask) || (mask & Occupied) != 0)
        {
            return false;
        }

        for (int cell = 0; cell < BagGrid.CellCount; cell++)
        {
            if ((mask & (1UL << cell)) != 0)
            {
                Owners[cell] = (sbyte)anchor;
            }
        }

        Footprints[anchor] = footprint;
        Occupied |= mask;
        ItemCount++;
        return true;
    }

    // Takes the item anchored at anchor off the board; false when no item is anchored there.
    public bool Remove(int anchor)
    {
        if (!IsAnchor(anchor))
        {
            return false;
        }

        for (int cell = 0; cell < BagGrid.CellCount; cell++)
        {
            if (Owners[cell] == anchor)
            {
                Owners[cell] = -1;
                Occupied &= ~(1UL << cell);
            }
        }

        Footprints[anchor] = default;
        ItemCount--;
        return true;
    }

    // The lowest anchor (row-major) the footprint fits at on the free cells, or -1.
    public int FirstFit(ItemFootprint footprint) => BagGrid.FirstFit(Occupied, footprint);

    // True when the footprint fits with its top-left cell at anchor on free cells only.
    public bool Fits(int anchor, ItemFootprint footprint)
        => BagGrid.TryMask(anchor, footprint, out ulong mask) && (mask & Occupied) == 0;

    public BagMovePlan PlanMove(int from, int to) => BagGrid.PlanMove(Owners, Footprints, from, to);
}
