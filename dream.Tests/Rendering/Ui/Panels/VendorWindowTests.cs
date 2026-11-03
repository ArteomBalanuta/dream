using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

// M3: the vendor window. It draws only with the inventory and a vendor open, shows the stock and the
// wallet, turns a click on a stock row into a Buy pick and a click on Sell into a Sell pick of the
// selected bag item, pages the sell list, and drops a selection whose item is gone.
public sealed class VendorWindowTests : IDisposable
{
    private readonly InventoryScene _scene = new();

    private sealed class VendorActions : IHudActions
    {
        public List<VendorHudAction> Picked { get; } = new();

        public int VendorCloses { get; private set; }

        public void Inventory(InventoryHudAction action)
        {
        }

        public void CloseInventory()
        {
        }

        public void Vendor(VendorHudAction action) => Picked.Add(action);

        public void CloseVendor() => VendorCloses++;

        public List<long> PickedUp { get; } = new();

        public void PickUpGround(long itemInstanceId) => PickedUp.Add(itemInstanceId);
    }

    private readonly VendorActions _actions = new();
    private readonly VendorHudState _vendor = new();

    public VendorWindowTests()
    {
        _scene.View.Vendor = _vendor;
    }

    public void Dispose() => _scene.Dispose();

    private void OpenVendor(params VendorStockLine[] stock) => _vendor.Open(900, "Old Mara", stock);

    private void SetGold(long gold) =>
        _scene.View.Inventory = _scene.View.Inventory!.Value with { Gold = gold };

    private void Frame()
    {
        _scene.Canvas.Calls.Clear();
        VendorWindow.Build(_scene.Canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _actions);
        _scene.Input.EndFrame();
    }

    private UiRect Units(float x, float y, float w, float h) =>
        _scene.Layout.FromUnits(_scene.Layout.Character, x, y, w, h);

    private void Click(UiRect rect)
    {
        _scene.Input.Pointer = InventoryScene.Centre(rect);
        _scene.Input.LeftClick = true;
        Frame();
    }

    private UiRect BuyRow(int n) => Units(19f, 58f + (n * 13f), 152f, 12f);

    private UiRect SellRow(int n) => Units(19f, 182f + (n * 13f), 152f, 12f);

    private UiRect SellButton() => Units(19f, 182f + (8 * 13f) + 6f, 152f, 16f);

    private void AddBagItems(int count)
    {
        for (int n = 0; n < count; n++)
        {
            _scene.Items[n] = InventoryScene.Item(100 + n, "gem.amber");
        }

        _scene.Refresh();
    }

    [Fact]
    public void ADrawnWindowNeedsAnOpenVendorAndAnOpenInventory()
    {
        Frame();
        Assert.Empty(_scene.Canvas.Calls);

        OpenVendor(new VendorStockLine("tool.torch", "Torch", 12));
        _scene.View.InventoryOpen = false;
        Frame();
        Assert.Empty(_scene.Canvas.Calls);

        _scene.View.InventoryOpen = true;
        Frame();
        Assert.True(_scene.Canvas.HasText("Old Mara"));
    }

    [Fact]
    public void TheWindowShowsTheStockWithPricesAndTheWallet()
    {
        OpenVendor(new VendorStockLine("tool.torch", "Torch", 12), new VendorStockLine("tool.field-blade", "Field Blade", 300));
        SetGold(50);

        Frame();

        Assert.True(_scene.Canvas.HasText("Gold 50"));
        Assert.True(_scene.Canvas.HasText("Torch"));
        Assert.True(_scene.Canvas.HasText("Field Blade"));
        // Each text is drawn twice (shadow first), so the last call is the coloured one.
        CanvasCall affordable = _scene.Canvas.Texts.Last(t => t.Text == "12");
        CanvasCall tooDear = _scene.Canvas.Texts.Last(t => t.Text == "300");
        Assert.NotEqual(affordable.Color, tooDear.Color);
        Assert.Equal(ItemTierStyle.Danger, tooDear.Color);
    }

    [Fact]
    public void ClickingAStockRowPicksABuyOfThatKind()
    {
        OpenVendor(new VendorStockLine("tool.torch", "Torch", 12), new VendorStockLine("tool.field-blade", "Field Blade", 300));

        Click(BuyRow(1));

        VendorHudAction pick = Assert.Single(_actions.Picked);
        Assert.Equal(VendorHudActionKind.Buy, pick.Kind);
        Assert.Equal("tool.field-blade", pick.ItemKind);
    }

    [Fact]
    public void SellNeedsASelection_ThenPicksTheSelectedItemAndClearsIt()
    {
        OpenVendor();
        AddBagItems(3);

        Click(SellButton());
        Assert.Empty(_actions.Picked);

        Click(SellRow(1));
        Assert.Equal(1, _vendor.SelectedAnchor);
        Assert.Equal(101, _vendor.SelectedItemId);
        Assert.Empty(_actions.Picked);

        Click(SellButton());
        VendorHudAction pick = Assert.Single(_actions.Picked);
        Assert.Equal(VendorHudActionKind.Sell, pick.Kind);
        Assert.Equal(1, pick.SlotIndex);
        Assert.Equal(101, pick.ItemInstanceId);
        Assert.Equal(-1, _vendor.SelectedAnchor);

        Click(SellButton());
        Assert.Single(_actions.Picked);
    }

    [Fact]
    public void WornAndMultiCellItemsAreListedOncePerItem_AndWornAreSkipped()
    {
        OpenVendor();
        _scene.Items[0] = InventoryScene.Item(100, "gem.amber");
        _scene.Items[1] = InventoryScene.Item(101, "gem.amber");
        _scene.Equipped[1] = true;
        _scene.Refresh();

        Assert.Equal(1, VendorWindow.CountSellable(_scene.View));
    }

    [Fact]
    public void TheSellListPages_AndThePageSticksInRange()
    {
        OpenVendor();
        AddBagItems(10);

        Frame();
        Assert.True(_scene.Canvas.HasText("1/2"));
        Assert.True(_scene.Canvas.HasText("Item 107"));
        Assert.False(_scene.Canvas.HasText("Item 108"));

        UiRect next = Units(19f + 152f - 14f, 168f, 14f, 12f);
        Click(next);
        Frame();
        Assert.Equal(1, _vendor.SellPage);
        Assert.True(_scene.Canvas.HasText("2/2"));
        Assert.True(_scene.Canvas.HasText("Item 108"));
        Assert.False(_scene.Canvas.HasText("Item 100"));

        _vendor.SellPage = 40;
        Frame();
        Assert.Equal(1, _vendor.SellPage);

        _vendor.SellPage = -3;
        Frame();
        Assert.Equal(0, _vendor.SellPage);
    }

    [Fact]
    public void ASelectionWhoseItemIsGoneIsDropped()
    {
        OpenVendor();
        AddBagItems(2);
        Click(SellRow(0));
        Assert.Equal(0, _vendor.SelectedAnchor);

        _scene.Items[0] = null;
        _scene.Refresh();
        Frame();

        Assert.Equal(-1, _vendor.SelectedAnchor);
        Click(SellButton());
        Assert.Empty(_actions.Picked);
    }

    [Fact]
    public void TheCloseButtonClosesTheVendor()
    {
        OpenVendor();

        Click(Units(19f, 395f, 24f, 24f));

        Assert.Equal(1, _actions.VendorCloses);
    }

    [Fact]
    public void HostileNamesAreCutAndStayLiteral()
    {
        string hostile = "%s{0}" + new string('x', 400);
        _scene.Items[0] = InventoryScene.Item(100, "gem.amber") with { DisplayName = hostile };
        _scene.Refresh();
        _vendor.Open(900, new string('T', 500), [new VendorStockLine("tool.torch", hostile, long.MaxValue)]);

        Frame();

        Assert.All(_scene.Canvas.Texts, t => Assert.True(t.Text.Length <= 64, t.Text));
        Assert.Contains(_scene.Canvas.Texts, t => t.Text.StartsWith("%s{0}xxx", StringComparison.Ordinal));
    }

    [Fact]
    public void ClassicHudShowsTheVendorInPlaceOfTheCharacterWindow()
    {
        OpenVendor(new VendorStockLine("tool.torch", "Torch", 12));
        InventoryFakeCanvas canvas = new(_scene.Table);

        ClassicHud.Build(canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _actions);

        Assert.True(canvas.HasText("Old Mara"));
        Assert.False(canvas.HasText("Character"));

        _vendor.Close();
        InventoryFakeCanvas plain = new(_scene.Table);
        ClassicHud.Build(plain, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _actions);

        Assert.True(plain.HasText("Character"));
        Assert.False(plain.HasText("Old Mara"));
    }

    [Fact]
    public void AClickInAVendorRowGap_SendsNoPickupForAGroundLabelBehindTheWindow()
    {
        OpenVendor(new VendorStockLine("tool.torch", "Torch", 12), new VendorStockLine("tool.field-blade", "Field Blade", 300));
        // The gap between the first two stock rows (rows are 12 units tall on a 13 unit pitch).
        UiRect gap = Units(19f, 58f + 12f, 152f, 1f);
        Vector2 spot = InventoryScene.Centre(gap);
        _scene.View.Labels = [new HudWorldLabel(spot, "Torch", "", new HudLabelClick(HudLabelClickKind.PickUpGround, 77))];
        _scene.Input.Pointer = spot;
        _scene.Input.LeftClick = true;

        _scene.Canvas.Calls.Clear();
        WorldLabelsPanel.Build(_scene.Canvas, _scene.Input, _scene.View, _scene.Layout, _actions);
        _scene.Input.EndFrame();

        Assert.Empty(_actions.PickedUp);

        // With the windows closed the same click does pick the item up.
        _scene.View.InventoryOpen = false;
        _scene.Input.LeftClick = true;
        WorldLabelsPanel.Build(_scene.Canvas, _scene.Input, _scene.View, _scene.Layout, _actions);
        Assert.Equal([77L], _actions.PickedUp);
    }
}
