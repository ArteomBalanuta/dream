using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

// The grid shop's pure geometry and rules: the board mirrors the bag cell for cell, stock is placed by
// footprint in stock order, a shop cell's identity is the item kind, and a release over the board is
// Valid, Invalid or None exactly as the contract freezes it.
public sealed class VendorGridTests : IDisposable
{
    private readonly InventoryScene _scene = new();
    private readonly VendorHudState _vendor = new();

    public VendorGridTests()
    {
        _scene.View.Vendor = _vendor;
    }

    public void Dispose() => _scene.Dispose();

    private void Open(params VendorStockLine[] stock) => _vendor.Open(900, "Old Mara", stock);

    private Vector2 OverBoard() =>
        InventoryScene.Centre(VendorGrid.CellRect(_scene.Layout, _scene.Layout.Character, 27));

    private InventoryWindow.DropVerdict Resolve(int source, Vector2 p, out VendorHudAction action) =>
        VendorGrid.TryResolveSell(_scene.Layout, _scene.View, source, p, out action);

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(1100, 700)]
    public void TheBoardMirrorsTheBagCellForCell(int w, int h)
    {
        UiLayout l = UiLayout.Create(w, h, 0f);
        for (int cell = 0; cell < BagGrid.CellCount; cell++)
        {
            UiRect shop = VendorGrid.CellRect(l, l.Character, cell);
            UiRect bag = InventoryWindow.SlotRect(l, l.Inventory, cell);
            Assert.Equal(bag.X - l.Inventory.X, shop.X - l.Character.X, 0.01f);
            Assert.Equal(bag.Y - l.Inventory.Y, shop.Y - l.Character.Y, 0.01f);
            Assert.Equal(bag.W, shop.W, 0.01f);
            Assert.Equal(bag.H, shop.H, 0.01f);
        }

        UiRect board = VendorGrid.BoardRect(l, l.Character);
        UiRect bagBoard = InventoryWindow.BagRect(l, l.Inventory);
        Assert.Equal(bagBoard.W, board.W, 0.01f);
        Assert.Equal(bagBoard.H, board.H, 0.01f);
        Assert.Equal(15f, VendorGrid.LeftUnits);
        Assert.Equal(200f, VendorGrid.TopUnits);
        UiRect item = VendorGrid.ItemRect(l, l.Character, 18, new ItemFootprint(2, 3));
        UiRect bagItem = InventoryWindow.ItemRect(l, l.Inventory, 18, new ItemFootprint(2, 3));
        Assert.Equal(bagItem.W, item.W, 0.01f);
        Assert.Equal(bagItem.H, item.H, 0.01f);
    }

    [Fact]
    public void Place_PutsStockFirstFitInStockOrder_ByRealFootprints()
    {
        // 2x3 blade, 1x1 amber, 2x2 helm: the blade takes cols 0-1 rows 0-2, the amber the first free cell
        // to its right, the helm the first 2x2 gap.
        Open(
            new VendorStockLine("axe.twinbit", "Axe", 10),
            new VendorStockLine("gem.amber", "Amber", 5),
            new VendorStockLine("helm.gilded", "Helm", 7));
        Span<int> anchors = stackalloc int[VendorHudState.MaxStock];
        Span<ItemFootprint> footprints = stackalloc ItemFootprint[VendorHudState.MaxStock];

        int placed = VendorGrid.Place(_scene.View, _vendor, anchors, footprints);

        Assert.Equal(3, placed);
        Assert.Equal(0, anchors[0]);
        Assert.Equal(new ItemFootprint(2, 3), footprints[0]);
        Assert.Equal(2, anchors[1]);
        Assert.Equal(3, anchors[2]);
        Assert.Equal(-1, anchors[3]);
    }

    [Fact]
    public void Place_ALineThatDoesNotFitIsMinusOne_AndAMalformedFootprintDoesNotBlockTheRest()
    {
        // Four 4x4 slabs fill the 8x8 board, so the amber after them has no room; a 9x9 footprint is
        // malformed and takes no cells, so the amber after it still places.
        _scene.Footprints["huge.slab"] = new ItemFootprint(4, 4);
        _scene.Footprints["bad.slab"] = new ItemFootprint(9, 9);
        Open(
            new VendorStockLine("huge.slab", "A", 1),
            new VendorStockLine("huge.slab", "B", 1),
            new VendorStockLine("bad.slab", "Bad", 1),
            new VendorStockLine("huge.slab", "C", 1),
            new VendorStockLine("huge.slab", "D", 1),
            new VendorStockLine("gem.amber", "Amber", 5));
        Span<int> anchors = stackalloc int[VendorHudState.MaxStock];
        Span<ItemFootprint> footprints = stackalloc ItemFootprint[VendorHudState.MaxStock];

        int placed = VendorGrid.Place(_scene.View, _vendor, anchors, footprints);

        Assert.Equal(4, placed);
        Assert.Equal([0, 4, -1, 32, 36, -1], anchors[..6].ToArray());
    }

    [Fact]
    public void Place_WithNoStockPlacesNothing_AndShortSpansNeverThrow()
    {
        Open();
        Span<int> anchors = stackalloc int[VendorHudState.MaxStock];
        Span<ItemFootprint> footprints = stackalloc ItemFootprint[VendorHudState.MaxStock];
        Assert.Equal(0, VendorGrid.Place(_scene.View, _vendor, anchors, footprints));

        Open(new VendorStockLine("gem.amber", "A", 1), new VendorStockLine("gem.amber", "B", 1), new VendorStockLine("gem.amber", "C", 1));
        Span<int> small = stackalloc int[2];
        Span<ItemFootprint> smallFp = stackalloc ItemFootprint[2];
        Assert.Equal(2, VendorGrid.Place(_scene.View, _vendor, small, smallFp));
    }

    [Fact]
    public void Place_AllocatesNothing()
    {
        // The shipped catalog's footprints, as in play: the scene's own lookup builds a string per call.
        _scene.View.FootprintOf = BagGrid.FootprintOf;
        Open(new VendorStockLine("axe.twinbit", "Axe", 10), new VendorStockLine("gem.amber", "Amber", 5));
        long bytes = Dream.Tests.Utilities.AllocationAssert.AtMost(0, () =>
        {
            Span<int> anchors = stackalloc int[VendorHudState.MaxStock];
            Span<ItemFootprint> footprints = stackalloc ItemFootprint[VendorHudState.MaxStock];
            VendorGrid.Place(_scene.View, _vendor, anchors, footprints);
        });

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void KindIdentity_IsStableAndDistinguishesKinds()
    {
        Assert.Equal(VendorGrid.KindIdentity("tool.torch"), VendorGrid.KindIdentity(new string("tool.torch".AsSpan())));
        Assert.NotEqual(VendorGrid.KindIdentity("tool.torch"), VendorGrid.KindIdentity("tool.torch2"));
        Assert.NotEqual(VendorGrid.KindIdentity("gem.amber"), VendorGrid.KindIdentity("gem.ambre"));
        Assert.Equal(0L, VendorGrid.KindIdentity(null));
        // FNV-1a 64 of the empty string is its offset basis, and of "a" a published value: not a per-process hash.
        Assert.Equal(unchecked((long)14695981039346656037UL), VendorGrid.KindIdentity(string.Empty));
        Assert.Equal(unchecked((long)0xAF63DC4C8601EC8CUL), VendorGrid.KindIdentity("a"));
    }

    [Fact]
    public void OverShop_NeedsAnOpenVendorAndAFinitePointerOnTheBoard()
    {
        Vector2 on = OverBoard();
        Assert.False(VendorGrid.OverShop(_scene.Layout, _scene.View, on));

        Open(new VendorStockLine("gem.amber", "Amber", 5));
        Assert.True(VendorGrid.OverShop(_scene.Layout, _scene.View, on));
        Assert.False(VendorGrid.OverShop(_scene.Layout, _scene.View, new Vector2(1f, 1f)));
        Assert.False(VendorGrid.OverShop(_scene.Layout, _scene.View, new Vector2(float.NaN, on.Y)));
        Assert.False(VendorGrid.OverShop(_scene.Layout, _scene.View, new Vector2(on.X, float.PositiveInfinity)));

        _vendor.Close();
        Assert.False(VendorGrid.OverShop(_scene.Layout, _scene.View, on));
    }

    [Fact]
    public void TryResolveSell_ABagItemOverTheBoard_IsAValidSellOfThatItem()
    {
        _scene.Items[9] = InventoryScene.Item(77, "gem.amber");
        _scene.Refresh();
        Open(new VendorStockLine("gem.amber", "Amber", 5));

        InventoryWindow.DropVerdict verdict = Resolve(9, OverBoard(), out VendorHudAction action);

        Assert.Equal(InventoryWindow.DropVerdict.Valid, verdict);
        Assert.Equal(new VendorHudAction(VendorHudActionKind.Sell, null, 9, 77), action);
    }

    [Fact]
    public void TryResolveSell_AWellSource_IsInvalid()
    {
        _scene.Worn[2] = InventoryScene.Item(5, "helm.gilded", EquipmentSlots.MaskOf(EquipmentSlot.Head));
        _scene.Refresh();
        Open(new VendorStockLine("gem.amber", "Amber", 5));

        InventoryWindow.DropVerdict verdict = Resolve(EquipmentSlots.ToSlotIndex(EquipmentSlot.Head), OverBoard(), out VendorHudAction action);

        Assert.Equal(InventoryWindow.DropVerdict.Invalid, verdict);
        Assert.Equal(default, action);
    }

    [Fact]
    public void TryResolveSell_ABagItemThatIsAlsoWorn_IsInvalid()
    {
        _scene.Items[4] = InventoryScene.Item(5, "helm.gilded", EquipmentSlots.MaskOf(EquipmentSlot.Head));
        _scene.Worn[2] = InventoryScene.Item(5, "helm.gilded", EquipmentSlots.MaskOf(EquipmentSlot.Head));
        _scene.Refresh();
        Open(new VendorStockLine("gem.amber", "Amber", 5));

        Assert.Equal(InventoryWindow.DropVerdict.Invalid, Resolve(4, OverBoard(), out VendorHudAction action));
        Assert.Equal(default, action);
    }

    [Fact]
    public void TryResolveSell_AnythingElse_IsNone()
    {
        _scene.Items[9] = InventoryScene.Item(77, "gem.amber");
        _scene.Refresh();

        // No vendor open.
        Assert.Equal(InventoryWindow.DropVerdict.None, Resolve(9, OverBoard(), out _));

        Open(new VendorStockLine("gem.amber", "Amber", 5));
        // Off the board, a non-finite pointer, an empty anchor, nothing dragged.
        Assert.Equal(InventoryWindow.DropVerdict.None, Resolve(9, new Vector2(2f, 2f), out _));
        Assert.Equal(InventoryWindow.DropVerdict.None, Resolve(9, new Vector2(float.NaN, float.NaN), out _));
        Assert.Equal(InventoryWindow.DropVerdict.None, Resolve(30, OverBoard(), out _));
        Assert.Equal(InventoryWindow.DropVerdict.None, Resolve(-1, OverBoard(), out _));
    }
}
