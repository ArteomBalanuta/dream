using Dream.Simulation.Content;

namespace Dream.Simulation.Inventory;

// The size of an item in bag cells: Cols wide, Rows tall, each 1..BagGrid.MaxFootprint. An item is
// placed by its top-left anchor cell (row * 8 + col); there is no rotation.
public readonly record struct ItemFootprint(byte Cols, byte Rows)
{
    public static ItemFootprint One => new(1, 1);
}

public enum BagMoveVerdict : byte
{
    // The item is dropped on its own anchor: nothing changes.
    Same,
    // The item goes to the target anchor, which is free (apart from the item's own old cells).
    Move,
    // The item goes to the target anchor and the one item it overlaps goes elsewhere (BagMovePlan).
    Swap,
    // Not allowed: out of bounds, two or more items overlapped, nowhere for a swapped item to go, or a
    // malformed request. The server answers NoRoom; the client shows the invalid highlight.
    Blocked,
}

// What a drop would do. OtherAnchor and OtherTo are -1 unless Verdict is Swap: then OtherAnchor is the
// anchor of the item that gets displaced and OtherTo is where it lands (the mover's old anchor if it
// fits there, else the first fit).
public readonly record struct BagMovePlan(BagMoveVerdict Verdict, int OtherAnchor, int OtherTo);

// The 8x8 bag board. Pure and allocation free; the server rules and the client drop verdict both use
// it, so they cannot diverge.
//
// A board is described by two inputs:
//   - occupancy, a ulong with bit (row * 8 + col) set for every covered cell, and
//   - owners, 64 sbyte entries (one per cell) holding the anchor of the item covering that cell, or -1
//     for an empty cell, plus footprints, 64 entries indexed by anchor.
//
// Positions are anchors, 0..63. Anything out of range, malformed or hostile yields false, -1 or
// Blocked; nothing here throws.
public static class BagGrid
{
    public const int Columns = 8;
    public const int Rows = 8;
    public const int CellCount = Columns * Rows;
    public const int MaxFootprint = 4;

    // The footprint of an item kind from the shipped content catalog; One for a kind the catalog does
    // not know (or null). Allocates nothing once the catalog is loaded.
    public static ItemFootprint FootprintOf(string? itemKind)
        => itemKind != null && ContentCatalog.Shipped.TryGetItem(itemKind, out ItemDefinition definition)
            ? definition.Footprint
            : ItemFootprint.One;

    // The cells a footprint covers when anchored at anchor, as a bit per cell (bit = row * 8 + col).
    // False (mask 0) when the anchor is outside 0..63, a side is 0 or above MaxFootprint, or the item
    // would run past the right or bottom edge. A true mask never has a bit beyond bit 63.
    public static bool TryMask(int anchor, ItemFootprint f, out ulong mask)
    {
        mask = 0;
        if ((uint)anchor >= CellCount
            || f.Cols == 0 || f.Cols > MaxFootprint
            || f.Rows == 0 || f.Rows > MaxFootprint)
        {
            return false;
        }

        int col = anchor % Columns;
        int row = anchor / Columns;
        if (col + f.Cols > Columns || row + f.Rows > Rows)
        {
            return false;
        }

        // One row of the footprint, then that row shifted down. The checks above bound every shift:
        // the row mask ends at column 7 at most and the last row index is 7, so the highest bit is 63.
        ulong rowBits = ((1UL << f.Cols) - 1UL) << col;
        ulong result = 0;
        for (int r = 0; r < f.Rows; r++)
        {
            result |= rowBits << ((row + r) * Columns);
        }

        mask = result;
        return true;
    }

    // The lowest anchor (row-major) where the footprint fits on the free cells, or -1 when it fits
    // nowhere (including a malformed footprint).
    public static int FirstFit(ulong occupied, ItemFootprint f)
    {
        if (!TryMask(0, f, out ulong atOrigin))
        {
            return -1;
        }

        int lastRow = Rows - f.Rows;
        int lastCol = Columns - f.Cols;
        for (int row = 0; row <= lastRow; row++)
        {
            for (int col = 0; col <= lastCol; col++)
            {
                int anchor = (row * Columns) + col;

                // The mask at anchor 0, moved by the anchor, is the mask at the anchor: the item stays
                // inside the board here, so no bit crosses a row end or falls off bit 63.
                if ((occupied & (atOrigin << anchor)) == 0)
                {
                    return anchor;
                }
            }
        }

        return -1;
    }

    // What dropping the item anchored at from onto the anchor to would do (decision D10).
    //
    // The item keeps its footprint and is placed with its top-left cell on to. Its own old cells do not
    // count as obstacles, so a short shift inside its own area is a plain Move. If the new area covers
    // exactly one other item the two swap: the mover takes the spot, the other goes to the mover's old
    // anchor when it fits there (with the mover already in its new place), else to the first fit of the
    // board with both items lifted off, else the drop is Blocked. Two or more other items under the new
    // area is Blocked. Atomic by construction: the plan changes nothing; the caller applies it whole.
    //
    // owners has CellCount entries (an anchor, or negative for an empty cell) and footprints has
    // CellCount entries by anchor. Blocked for a short span, an out-of-range from or to, a from that is
    // not the anchor of an item, a malformed footprint (also one that runs off the board from where the
    // item sits), or an owner value that is no anchor.
    public static BagMovePlan PlanMove(
        ReadOnlySpan<sbyte> owners,
        ReadOnlySpan<ItemFootprint> footprints,
        int from,
        int to)
    {
        BagMovePlan blocked = new(BagMoveVerdict.Blocked, -1, -1);
        if (owners.Length < CellCount
            || footprints.Length < CellCount
            || (uint)from >= CellCount
            || (uint)to >= CellCount
            || owners[from] != from
            || !TryMask(from, footprints[from], out _)
            || !TryMask(to, footprints[from], out ulong newArea))
        {
            return blocked;
        }

        if (from == to)
        {
            return new BagMovePlan(BagMoveVerdict.Same, -1, -1);
        }

        // Cells covered by anything but the mover, and the one other item under the new area, if any.
        // An owner above 63 is not an anchor: it covers its cell (so nothing is placed there) and is
        // never a swap partner.
        ulong occupiedByOthers = 0;
        for (int cell = 0; cell < CellCount; cell++)
        {
            sbyte owner = owners[cell];
            if (owner >= 0 && owner != from)
            {
                occupiedByOthers |= 1UL << cell;
            }
        }

        int other = -1;
        for (int cell = 0; cell < CellCount; cell++)
        {
            if ((newArea & (1UL << cell)) == 0)
            {
                continue;
            }

            sbyte owner = owners[cell];
            if (owner < 0 || owner == from)
            {
                continue;
            }

            if (owner >= CellCount || (other >= 0 && owner != other))
            {
                return blocked;
            }

            other = owner;
        }

        if (other < 0)
        {
            return new BagMovePlan(BagMoveVerdict.Move, -1, -1);
        }

        // Swap: lift the other item off the board, put the mover in its new place, then find the other a
        // spot. Its own old cells are free now; the mover's old cells are free too.
        if (owners[other] != other)
        {
            return blocked;
        }

        ulong occupiedWithoutOther = occupiedByOthers & ~OwnedBy(owners, other);
        ulong occupiedAfterMover = occupiedWithoutOther | newArea;
        if (TryMask(from, footprints[other], out ulong atMoverOldAnchor) && (atMoverOldAnchor & occupiedAfterMover) == 0)
        {
            return new BagMovePlan(BagMoveVerdict.Swap, other, from);
        }

        int fit = FirstFit(occupiedAfterMover, footprints[other]);
        return fit < 0
            ? blocked
            : new BagMovePlan(BagMoveVerdict.Swap, other, fit);
    }

    // The cells whose owner is the given anchor.
    private static ulong OwnedBy(ReadOnlySpan<sbyte> owners, int anchor)
    {
        ulong cells = 0;
        for (int cell = 0; cell < CellCount; cell++)
        {
            if (owners[cell] == anchor)
            {
                cells |= 1UL << cell;
            }
        }

        return cells;
    }
}
