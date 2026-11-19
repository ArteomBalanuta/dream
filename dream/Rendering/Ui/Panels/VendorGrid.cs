using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The grid shop's geometry and rules, pure and allocation-free. The board mirrors the bag exactly: the
// same origin in the window, the same 8x8 cells of 20 u, so the two boards line up pixel for pixel when
// the windows sit side by side. Stock is placed by real item footprints through BagGrid.FirstFit, in stock
// order, so the shop obeys the same rules as the bag.
public static class VendorGrid
{
    public const float LeftUnits = InventoryWindow.BagLeftUnits;
    public const float TopUnits = InventoryWindow.BagTopUnits;
    public const int Columns = BagGrid.Columns;
    public const int Rows = BagGrid.Rows;
    public const float CellUnits = InventoryWindow.CellUnits;

    public static UiRect BoardRect(in UiLayout l, UiRect win) =>
        l.FromUnits(win, LeftUnits, TopUnits, Columns * CellUnits, Rows * CellUnits);

    public static UiRect CellRect(in UiLayout l, UiRect win, int cell) =>
        l.FromUnits(win, LeftUnits + (cell % Columns * CellUnits), TopUnits + (cell / Columns * CellUnits), CellUnits, CellUnits);

    // The board cell under p (0..63), or -1 off the board (or for a non-finite p). This is the bag's own
    // arithmetic, called on the shop's rect, so the two boards cannot drift apart.
    internal static int CellAt(in UiLayout l, UiRect win, Vector2 p) =>
        InventoryWindow.CellIn(BoardRect(in l, win), in l, p);

    public static UiRect ItemRect(in UiLayout l, UiRect win, int anchor, ItemFootprint f) =>
        l.FromUnits(
            win,
            LeftUnits + (anchor % Columns * CellUnits),
            TopUnits + (anchor / Columns * CellUnits),
            f.Cols * CellUnits,
            f.Rows * CellUnits);

    // anchors[i] is stock line i's top-left cell, or -1 when it does not fit (or the span is too short to
    // hold it); footprints[i] is its footprint. Both spans are cleared over the shorter of the two lengths, which is also the most lines placed. Returns how many lines were placed. The caller passes
    // stackalloc spans of length VendorHudState.MaxStock.
    public static int Place(HudView v, VendorHudState vendor, Span<int> anchors, Span<ItemFootprint> footprints)
    {
        int count = vendor.StockCount;
        int room = Math.Min(anchors.Length, footprints.Length);
        ulong occupied = 0;
        int placed = 0;
        for (int n = 0; n < room; n++)
        {
            anchors[n] = -1;
            footprints[n] = default;
        }

        for (int n = 0; n < count && n < room; n++)
        {
            ItemFootprint footprint = v.FootprintOf(vendor.StockAt(n).ItemKind);
            footprints[n] = footprint;
            int anchor = BagGrid.FirstFit(occupied, footprint);
            if (anchor < 0 || !BagGrid.TryMask(anchor, footprint, out ulong mask))
            {
                continue;
            }

            occupied |= mask;
            anchors[n] = anchor;
            placed++;
        }

        return placed;
    }

    // Stable FNV-1a over the kind's characters, for HudWidgetId.Of (BH-INPUT-3): a shop cell's id carries
    // WHAT it sells, so a stock renumbering between press and release drops the click and never moves it to
    // another item. string.GetHashCode is randomised per process and must not be used here.
    public static long KindIdentity(string? itemKind)
    {
        if (itemKind is null)
        {
            return 0;
        }

        ulong hash = 14695981039346656037UL;
        for (int n = 0; n < itemKind.Length; n++)
        {
            hash = (hash ^ itemKind[n]) * 1099511628211UL;
        }

        return unchecked((long)hash);
    }

    // The vendor is open and p is on the board of the vendor window (the character window's place).
    public static bool OverShop(in UiLayout l, HudView v, Vector2 p) =>
        v.Vendor is { IsOpen: true }
        && float.IsFinite(p.X)
        && float.IsFinite(p.Y)
        && BoardRect(in l, l.Character).Contains(p);

    // What releasing the dragged source over the shop does. Valid fills action (Kind is always Sell).
    // A well source is Invalid (the protocol sells a bag slot only), and so is a bag item that is also
    // worn (the server refuses it, the rule the ground drop uses). Anything not over the shop is None.
    internal static InventoryWindow.DropVerdict TryResolveSell(
        in UiLayout l, HudView v, int source, Vector2 p, out VendorHudAction action)
    {
        action = default;
        if (!OverShop(in l, v, p))
        {
            return InventoryWindow.DropVerdict.None;
        }

        if (EquipmentSlots.TryFromSlotIndex(source, out _))
        {
            return InventoryWindow.DropVerdict.Invalid;
        }

        if (!InventoryWindow.IsPlaced(v, source))
        {
            return InventoryWindow.DropVerdict.None;
        }

        long itemId = v.Backpack[source].Item.ItemInstanceId;
        if (v.Worn is { } worn)
        {
            for (int slot = 0; slot < worn.Length; slot++)
            {
                if (InventoryWindow.TryItem(worn, slot, out InventoryItemSnapshot held) && held.ItemInstanceId == itemId)
                {
                    return InventoryWindow.DropVerdict.Invalid;
                }
            }
        }

        action = new VendorHudAction(VendorHudActionKind.Sell, null, source, itemId);
        return InventoryWindow.DropVerdict.Valid;
    }
}
