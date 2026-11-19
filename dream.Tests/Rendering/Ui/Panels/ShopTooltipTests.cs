using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

// The shop board shows the bag's tooltip: the same ItemOverlays path, fed the ware's base definition,
// with the price as one extra dim line that a bag tooltip never has.
public sealed class ShopTooltipTests : IDisposable
{
    private readonly InventoryScene _scene = new();
    private readonly VendorHudState _vendor = new();
    private readonly ItemDefinition _ware;

    public ShopTooltipTests()
    {
        VendorFxState.Reset();
        _scene.View.Vendor = _vendor;
        // A shipped kind with a description and detail lines, so there is something to compare.
        _ware = ContentCatalog.Shipped.Items.First(d =>
            d.Footprint.Equals(ItemFootprint.One) && !string.IsNullOrEmpty(d.Description) && d.DetailLines.Count > 0);
    }

    public void Dispose()
    {
        _scene.Dispose();
        VendorFxState.Reset();
    }

    private sealed class NoActions : IHudActions
    {
        public void Inventory(InventoryHudAction action)
        {
        }

        public void CloseInventory()
        {
        }

        public void Vendor(VendorHudAction action)
        {
        }

        public void CloseVendor()
        {
        }

        public void PickUpGround(long itemInstanceId)
        {
        }
    }

    // The order ClassicHud builds in: the vendor window, the bag, then the overlays.
    private void Frame()
    {
        _scene.Canvas.Calls.Clear();
        NoActions actions = new();
        VendorWindow.Build(_scene.Canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, actions);
        InventoryWindow.Build(_scene.Canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, actions);
        ItemOverlays.Build(_scene.Canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, actions);
        _scene.Input.EndFrame();
    }

    private void Stock(string kind, long price = 30) =>
        _vendor.Open(900, "Old Mara", [new VendorStockLine(kind, _ware.DisplayName, price)]);

    private void HoverShopCell(int cell) =>
        _scene.Input.Pointer = InventoryScene.Centre(VendorGrid.CellRect(_scene.Layout, _scene.Layout.Character, cell));

    // The coloured (non-shadow) text lines the tooltip drew: everything after its panel.
    private List<string> TooltipLines()
    {
        int panel = _scene.Canvas.Calls.FindIndex(c => c.Name == "tip.panel");
        if (panel < 0)
        {
            return [];
        }

        return _scene.Canvas.Calls.Skip(panel + 1)
            .Where(c => c.Kind == CanvasCallKind.Text && c.Color != ItemTierStyle.Shadow)
            .Select(c => c.Text)
            .ToList();
    }

    [Fact]
    public void HoveringAWare_ShowsItsNameAndDetailLines()
    {
        Stock(_ware.Key);
        HoverShopCell(0);

        Frame();

        List<string> lines = TooltipLines();
        Assert.Contains(_ware.DisplayName, string.Join(" ", lines));
        Assert.Contains(lines, l => _ware.DetailLines[0].StartsWith(l, StringComparison.Ordinal));
    }

    [Fact]
    public void TheShopTooltip_IsTheBagTooltipOfTheSameKind_ApartFromThePrice()
    {
        Stock(_ware.Key);
        HoverShopCell(0);
        Frame();
        List<string> shop = TooltipLines();

        Assert.True(VendorWindow.TryWareSnapshot(_ware.Key, out InventoryItemSnapshot ware));
        _scene.Items[5] = ware with { ItemInstanceId = 77 };
        _scene.Refresh();
        _vendor.Close();
        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(5));
        Frame();
        List<string> bag = TooltipLines();

        Assert.NotEmpty(bag);
        Assert.Equal(bag, shop.Take(shop.Count - 1).ToList());
        Assert.Equal("Price 30 gold", shop[^1]);
    }

    [Fact]
    public void AnEmptyShopCell_ShowsNoTooltip()
    {
        Stock(_ware.Key);
        HoverShopCell(63);

        Frame();

        Assert.Empty(_scene.Canvas.Named("tip.panel"));
        Assert.Empty(TooltipLines());
    }

    [Fact]
    public void ThePriceLine_IsOnTheShopTooltipOnly()
    {
        Stock(_ware.Key, 41);
        HoverShopCell(0);
        Frame();
        Assert.Contains("Price 41 gold", TooltipLines());

        _vendor.Close();
        InventoryItemSnapshot item = InventoryScene.Item(31, "gear.iron-band");
        _scene.Items[5] = item;
        _scene.Refresh();
        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(5));
        Frame();

        Assert.NotEmpty(TooltipLines());
        Assert.DoesNotContain(TooltipLines(), l => l.StartsWith("Price", StringComparison.Ordinal));
    }

    [Fact]
    public void AWareOfAnUnknownKind_DrawsNoTooltipAndDoesNotThrow()
    {
        Stock("nothing.known");
        HoverShopCell(0);

        Frame();

        Assert.Empty(_scene.Canvas.Named("tip.panel"));
        Assert.Empty(TooltipLines());
    }

    [Fact]
    public void TheHover_DoesNotOutliveItsFrame()
    {
        Stock(_ware.Key);
        HoverShopCell(0);
        Frame();
        Assert.NotEmpty(TooltipLines());

        _scene.Input.Pointer = new Vector2(-1000f, -1000f);
        Frame();

        Assert.Empty(TooltipLines());
        Assert.False(InventoryUiState.Current.HoverItemActive);
    }

    [Fact]
    public void AWareSnapshotIdIsStableAndNegative()
    {
        Assert.True(VendorWindow.TryWareSnapshot(_ware.Key, out InventoryItemSnapshot a));
        Assert.True(VendorWindow.TryWareSnapshot(_ware.Key, out InventoryItemSnapshot b));
        Assert.True(a.ItemInstanceId < 0);
        Assert.Equal(a.ItemInstanceId, b.ItemInstanceId);
    }
}
