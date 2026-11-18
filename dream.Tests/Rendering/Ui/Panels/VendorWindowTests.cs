using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

// Wraps the scripted input and records the feedback sounds a panel asks for (the fake input drops them).
public sealed class ShopSpyInput(InventoryFakeInput inner) : IUiInput
{
    public List<UiFeedback> Emitted { get; } = new();

    public bool Button(int id, UiRect r) => inner.Button(id, r);

    public bool Hovered(int id, UiRect r) => inner.Hovered(id, r);

    public bool RightClicked(int id, UiRect r) => inner.RightClicked(id, r);

    public bool BeginDrag(int id, UiRect r, int payload) => inner.BeginDrag(id, r, payload);

    public bool TryEndDrag(out int payload, out Vector2 dropPosition) => inner.TryEndDrag(out payload, out dropPosition);

    public int ActiveDragPayload => inner.ActiveDragPayload;

    public bool ModalActive
    {
        get => inner.ModalActive;
        set => inner.ModalActive = value;
    }

    public UiPointer Pointer => ((IUiInput)inner).Pointer;

    public void Emit(UiFeedback feedback) => Emitted.Add(feedback);
}

// The grid shop: it draws only with the inventory and a vendor open, places the stock by footprint on a
// board that mirrors the bag, turns a click on an item into a Buy of that kind (and nothing else), refuses
// a price above the wallet, and drops a click whose stock changed between press and release.
public sealed class VendorWindowTests : IDisposable
{
    private readonly InventoryScene _scene = new();
    private readonly ShopSpyInput _input;

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
        VendorFxState.Reset();
        _scene.View.Vendor = _vendor;
        _input = new ShopSpyInput(_scene.Input);
    }

    public void Dispose()
    {
        _scene.Dispose();
        VendorFxState.Reset();
    }

    private void OpenVendor(params VendorStockLine[] stock) => _vendor.Open(900, "Old Mara", stock);

    private void SetGold(long gold) =>
        _scene.View.Inventory = _scene.View.Inventory!.Value with { Gold = gold };

    private void Frame()
    {
        _scene.Canvas.Calls.Clear();
        VendorWindow.Build(_scene.Canvas, _input, _scene.View, _scene.Layout, _scene.Table, _actions);
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

    // The rect of the item anchored at a cell of the SHOP board, 1x1 unless a footprint is given.
    private UiRect Shop(int anchor, ItemFootprint? f = null) =>
        VendorGrid.ItemRect(_scene.Layout, _scene.Layout.Character, anchor, f ?? ItemFootprint.One);

    private UiRect ShopCell(int cell) => VendorGrid.CellRect(_scene.Layout, _scene.Layout.Character, cell);

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
    public void TheBoardShowsTheStockWithPricesAndTheWallet_AndTheOldListsAreGone()
    {
        OpenVendor(new VendorStockLine("gem.amber", "Amber", 12), new VendorStockLine("gem.azure", "Azure", 300));
        SetGold(50);

        Frame();

        Assert.True(_scene.Canvas.HasText("Gold 50"));
        Assert.True(_scene.Canvas.HasText("For sale"));
        Assert.False(_scene.Canvas.HasText("Your goods"));
        Assert.False(_scene.Canvas.HasText("Sell"));
        // Each text is drawn twice (shadow first), so the last call is the coloured one.
        CanvasCall affordable = _scene.Canvas.Texts.Last(t => t.Text == "12");
        CanvasCall tooDear = _scene.Canvas.Texts.Last(t => t.Text == "300");
        Assert.NotEqual(affordable.Color, tooDear.Color);
        Assert.Equal(ItemTierStyle.Danger, tooDear.Color);
        // Icons, not names: the name is only the fallback for an item with no art.
        Assert.False(_scene.Canvas.HasText("Amber"));
        Assert.Single(_scene.Canvas.Named("item.gem.amber"));
    }

    [Fact]
    public void StockIsPlacedByFootprint_AndEachIconSitsOnItsItemsCells()
    {
        OpenVendor(
            new VendorStockLine("axe.twinbit", "Axe", 10),   // 2x3, cells 0..
            new VendorStockLine("gem.amber", "Amber", 5));   // 1x1, first free cell is 2

        Frame();

        CanvasCall blade = Assert.Single(_scene.Canvas.Named("item.axe.twinbit"));
        UiRect bladeBox = Shop(0, new ItemFootprint(2, 3));
        Assert.Equal(InventoryWindow.IconRect(_scene.Table.Get("item.axe.twinbit"), bladeBox, _scene.Layout.Scale), blade.Rect);
        CanvasCall amber = Assert.Single(_scene.Canvas.Named("item.gem.amber"));
        Assert.Equal(InventoryWindow.IconRect(_scene.Table.Get("item.gem.amber"), Shop(2), _scene.Layout.Scale), amber.Rect);
    }

    [Fact]
    public void AnItemWithNoArtShowsItsNameInItsCell()
    {
        OpenVendor(new VendorStockLine("nothing.known", "Mystery Relic", 9));

        Frame();

        Assert.Contains(_scene.Canvas.Texts, t => t.Text.StartsWith("Myst", StringComparison.Ordinal));
        Assert.True(_scene.Canvas.HasText("9"));
    }

    [Fact]
    public void EveryShopSpriteIsOptional_TheWindowDrawsWithoutThem()
    {
        // The art workstream lands the five sprites separately: a missing one is skipped, never a throw
        // and never a missing board (the bag's own cell art stands in).
        OpenVendor(new VendorStockLine("gem.amber", "Amber", 12));

        Frame();

        int cells = _scene.Canvas.Calls.Count(c => c.Name is "shop.cell.empty" or "cell.empty" or "shop.cell.occupied" or "cell.occupied");
        Assert.Equal(BagGrid.CellCount, cells);
    }

    [Fact]
    public void ClickingAnItemBuysThatKind_WithACoinAndNothingElse()
    {
        OpenVendor(new VendorStockLine("gem.amber", "Amber", 12), new VendorStockLine("gem.azure", "Azure", 30));
        SetGold(100);

        Click(Shop(1));

        VendorHudAction pick = Assert.Single(_actions.Picked);
        Assert.Equal(VendorHudActionKind.Buy, pick.Kind);
        Assert.Equal("gem.azure", pick.ItemKind);
        Assert.Equal([UiFeedback.Coin], _input.Emitted);
    }

    [Fact]
    public void ClickingAnEmptyCellBuysNothing()
    {
        OpenVendor(new VendorStockLine("gem.amber", "Amber", 12));
        SetGold(100);

        Click(ShopCell(40));
        Click(ShopCell(1));

        Assert.Empty(_actions.Picked);
        Assert.Empty(_input.Emitted);
    }

    [Fact]
    public void APriceAboveTheWalletIsRefused_AndABuyThatIsAffordableIsNot()
    {
        OpenVendor(new VendorStockLine("gem.amber", "Amber", 300));
        SetGold(299);

        Click(Shop(0));

        Assert.Empty(_actions.Picked);
        Assert.Equal([UiFeedback.Refuse], _input.Emitted);

        SetGold(300);
        Click(Shop(0));
        Assert.Equal("gem.amber", Assert.Single(_actions.Picked).ItemKind);
    }

    [Fact]
    public void NoClickBuysWhileAMenuHoldsTheWindowModal()
    {
        OpenVendor(new VendorStockLine("gem.amber", "Amber", 12));
        SetGold(100);
        _scene.Items[3] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();
        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(3));
        _scene.Input.RightClick = true;
        _scene.Frame();
        Assert.True(InventoryUiState.Current.ModalOpen);

        Click(Shop(0));

        Assert.Empty(_actions.Picked);
    }

    [Fact]
    public void ABuyedItemThatLandsInTheBag_FlashesItsBagCell()
    {
        OpenVendor(new VendorStockLine("gem.amber", "Amber", 12));
        SetGold(100);
        _scene.View.AnimationSeconds = 10f;
        Click(Shop(0));
        Assert.Equal(-1, VendorFxState.Current.FlashAnchor);

        // The server answers: the amber arrives on the first free bag cell.
        _scene.Items[0] = InventoryScene.Item(5, "gem.amber");
        _scene.Refresh();
        _scene.View.AnimationSeconds = 10.1f;
        _scene.Frame();

        Assert.Equal(0, VendorFxState.Current.FlashAnchor);
        Assert.InRange(VendorFxState.Current.FlashLevel(10.1f), 0.01f, 1f);
        if (_scene.Table.TryGet(UiSpriteNames.CellOverlayEmber, out _))
        {
            Assert.NotEmpty(_scene.Canvas.Named(UiSpriteNames.CellOverlayEmber));
        }

        // The flash is over after FlashSeconds.
        _scene.View.AnimationSeconds = 10.1f + VendorFxState.FlashSeconds + 0.05f;
        _scene.Frame();
        Assert.Equal(-1, VendorFxState.Current.FlashAnchor);
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
        _vendor.Open(900, new string('T', 500), [new VendorStockLine("gem.amber", hostile, long.MaxValue)]);

        // Hovered, the name and price show in the band above the board.
        _scene.Input.Pointer = InventoryScene.Centre(Shop(0));
        Frame();

        Assert.All(_scene.Canvas.Texts, t => Assert.True(t.Text.Length <= 64, t.Text));
        Assert.Contains(_scene.Canvas.Texts, t => t.Text.StartsWith("%s{0}xxx", StringComparison.Ordinal));
        Assert.True(_scene.Canvas.HasText("Price " + int.MaxValue));
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
    public void ClassicHudOnThisThread_IgnoresATradeAndADialogueAnotherThreadBound()
    {
        // A PlayScene test running at the same time binds ClassicHud.Trade and Quests with a window open.
        // They were process-wide, so the character window disappeared from this thread's HUD.
        Thread other = new(() =>
        {
            TradeHudState trade = new();
            trade.Open(TradeHudStage.Open, "Bob", default, 0, false, false, default, 0, false, false);
            ClassicHud.Trade = trade;
            QuestHudState quests = new();
            quests.OpenDialogue(5, "Elder", "Hello", null);
            ClassicHud.Quests = quests;
        });
        other.Start();
        other.Join();

        InventoryFakeCanvas plain = new(_scene.Table);
        ClassicHud.Build(plain, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _actions);

        Assert.True(plain.HasText("Character"));
        Assert.Null(ClassicHud.Trade);
        Assert.Null(ClassicHud.Quests);
    }

    [Fact]
    public void AClickInAShopGap_SendsNoPickupForAGroundLabelBehindTheWindow()
    {
        OpenVendor(new VendorStockLine("gem.amber", "Amber", 12), new VendorStockLine("gem.azure", "Azure", 30));
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

    // ---- BH-INPUT-3 on the shop cell: the REAL router, press then release ----------------------------

    private UiPointer At(Vector2 p, bool left) => new(p, left, false, true);

    private void RealFrame(UiInputRouter router, Vector2 at, bool left)
    {
        router.BeginFrame(At(at, left));
        _scene.Canvas.Calls.Clear();
        VendorWindow.Build(_scene.Canvas, router, _scene.View, _scene.Layout, _scene.Table, _actions);
        router.EndFrame();
    }

    private void PressAndRelease(Vector2 at, Action? betweenPressAndRelease)
    {
        UiInputRouter router = new();
        RealFrame(router, at, left: false);
        RealFrame(router, at, left: true);
        betweenPressAndRelease?.Invoke();
        RealFrame(router, at, left: false);
    }

    [Fact]
    public void AStableStock_BuysTheKindUnderThePointer_ThroughTheRealRouter()
    {
        OpenVendor(
            new VendorStockLine("gem.amber", "Amber", 5),
            new VendorStockLine("gem.azure", "Azure", 5),
            new VendorStockLine("gem.ruby", "Ruby", 5));
        SetGold(100);

        PressAndRelease(InventoryScene.Centre(Shop(1)), null);

        Assert.Equal("gem.azure", Assert.Single(_actions.Picked).ItemKind);
    }

    // BH-INPUT-3: the id carries the KIND, not the cell. The press is on the azure in cell 1; the amber is
    // withdrawn while the button is down, so the azure moves to cell 0 and the ruby comes under the
    // pointer. The release must never buy the ruby. (It still resolves to the azure the player pressed,
    // exactly as a held bag row keeps its own item.)
    [Fact]
    public void AStockThatRenumbersMidPress_NeverBuysTheItemThatMovedUnderThePointer()
    {
        OpenVendor(
            new VendorStockLine("gem.amber", "Amber", 5),
            new VendorStockLine("gem.azure", "Azure", 5),
            new VendorStockLine("gem.ruby", "Ruby", 5));
        SetGold(100);

        // The pointer is on the azure in cell 1. The amber is withdrawn while the button is down, so the
        // azure moves to cell 0 and the ruby comes under the pointer.
        PressAndRelease(
            InventoryScene.Centre(Shop(1)),
            () => OpenVendor(new VendorStockLine("gem.azure", "Azure", 5), new VendorStockLine("gem.ruby", "Ruby", 5)));

        Assert.DoesNotContain(_actions.Picked, p => p.ItemKind == "gem.ruby");
        Assert.All(_actions.Picked, p => Assert.Equal("gem.azure", p.ItemKind));
    }

    [Fact]
    public void ThePressedKindLeavingTheStockMidPress_DropsTheClickAltogether()
    {
        OpenVendor(
            new VendorStockLine("gem.amber", "Amber", 5),
            new VendorStockLine("gem.azure", "Azure", 5),
            new VendorStockLine("gem.ruby", "Ruby", 5));
        SetGold(100);

        PressAndRelease(
            InventoryScene.Centre(Shop(1)),
            () => OpenVendor(new VendorStockLine("gem.amber", "Amber", 5), new VendorStockLine("gem.ruby", "Ruby", 5)));

        Assert.Empty(_actions.Picked);
    }

    [Fact]
    public void TheShopCellIdFoldsTheKindIn_NotTheIndex()
    {
        // The same kind keeps its id wherever the stock puts it; two kinds never share one.
        int a = HudWidgetId.Of(VendorWindow.IdShopCell, VendorGrid.KindIdentity("gem.amber"));
        Assert.Equal(a, HudWidgetId.Of(VendorWindow.IdShopCell, VendorGrid.KindIdentity("gem.amber")));
        Assert.NotEqual(a, HudWidgetId.Of(VendorWindow.IdShopCell, VendorGrid.KindIdentity("gem.azure")));
        Assert.NotEqual(a, VendorWindow.IdShopCell);
    }
}
