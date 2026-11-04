using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

// Classic HUD iteration 2: twelve wells above the 8x8 footprint bag. The scene takes footprints from the
// item art, so a blade covers 2x3 cells here whatever the shipped items.json says.
public sealed class InventoryWindowTests : IDisposable
{
    private readonly InventoryScene _scene = new();

    private static readonly int HeadMask = EquipmentSlots.MaskOf(EquipmentSlot.Head);
    private static readonly int MainMask = EquipmentSlots.MaskOf(EquipmentSlot.MainHand);
    private static readonly int RingMask = EquipmentSlots.MaskOf(EquipmentSlot.RingLeft) | EquipmentSlots.MaskOf(EquipmentSlot.RingRight);
    private static readonly int HandsMask = EquipmentSlots.MaskOf(EquipmentSlot.MainHand) | EquipmentSlots.MaskOf(EquipmentSlot.OffHand);

    public void Dispose() => _scene.Dispose();

    // Pick up at one point, move, release at another. Two frames.
    private void Drag(Vector2 from, Vector2 to)
    {
        _scene.Input.Pointer = from;
        _scene.Input.StartDrag = true;
        _scene.Frame();
        _scene.Input.Pointer = to;
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();
    }

    private void Drag(UiRect from, UiRect to) => Drag(InventoryScene.Centre(from), InventoryScene.Centre(to));

    // Pick up and move, still holding: the frame shows the ghost and the drop highlight.
    private void Hold(Vector2 from, Vector2 to)
    {
        _scene.Input.Pointer = from;
        _scene.Input.StartDrag = true;
        _scene.Frame();
        _scene.Input.Pointer = to;
        _scene.Frame();
    }

    // A point given in units from the window's top-left.
    private Vector2 Units(float x, float y)
    {
        UiRect r = _scene.Layout.FromUnits(_scene.Layout.Inventory, x, y, 0f, 0f);
        return new Vector2(r.X, r.Y);
    }

    // A point given in cells from the board's top-left corner.
    private Vector2 BagPoint(float col, float row) => Units(15f + (col * 20f), 200f + (row * 20f));

    // Where the pointer goes so a footprint dropped there has its top-left cell on anchor: the middle of
    // the footprint placed at anchor.
    private Vector2 DropPoint(int anchor, int cols, int rows) =>
        BagPoint((anchor % 8) + (cols / 2f), (anchor / 8) + (rows / 2f));

    private static ItemFootprint F(int cols, int rows) => new((byte)cols, (byte)rows);

    private static IEnumerable<int> CellsOf(int anchor, int cols, int rows)
    {
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                yield return anchor + (r * 8) + c;
            }
        }
    }

    private HashSet<UiRect> CellRects(IEnumerable<int> cells) => cells.Select(_scene.Slot).ToHashSet();

    private HashSet<UiRect> RectsOf(string name) => _scene.Canvas.Named(name).Select(c => c.Rect).ToHashSet();

    // ---- icons ------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1, 40f, 40f, 1f)]
    [InlineData(1, 2, 40f, 40f, 1f)]
    [InlineData(2, 2, 40f, 40f, 1f)]
    [InlineData(2, 3, 40f, 40f, 2f / 3f)]
    [InlineData(2, 4, 40f, 40f, 0.5f)]
    [InlineData(3, 2, 40f, 40f, 2f / 3f)]
    [InlineData(2, 3, 40f, 60f, 1f)]
    [InlineData(2, 4, 40f, 60f, 0.75f)]
    [InlineData(3, 2, 60f, 40f, 1f)]
    [InlineData(0, 0, 40f, 40f, 1f)]
    public void FitScale_FitsTheFootprintAndNeverEnlarges(int cols, int rows, float boxW, float boxH, float expected)
    {
        Assert.Equal(expected, InventoryWindow.FitScale(cols, rows, boxW, boxH), 4);
    }

    [Fact]
    public void IconRect_IsCentredOnItsFootprintAndKeepsTheArtSize()
    {
        UiSprite axe = _scene.Table.Get("item.axe.twinbit");   // 208x288 px, footprint 2x3
        _scene.Items[5] = InventoryScene.Item(1, "axe.twinbit");
        UiRect box = _scene.ItemRect(5);

        UiRect r = InventoryWindow.IconRect(axe, box, _scene.Layout.Scale);

        Assert.Equal(40f * _scene.Layout.Scale, box.W, 3);
        Assert.Equal(60f * _scene.Layout.Scale, box.H, 3);
        Assert.Equal(208f / 4f * _scene.Layout.Scale, r.W, 3);
        Assert.Equal(288f / 4f * _scene.Layout.Scale, r.H, 3);
        Assert.Equal(box.X + (box.W / 2f), r.X + (r.W / 2f), 3);
        Assert.Equal(box.Y + (box.H / 2f), r.Y + (r.H / 2f), 3);
    }

    [Theory]
    [InlineData("gem.amber", 1, 1)]
    [InlineData("feather.azure", 1, 2)]
    [InlineData("helm.gilded", 2, 2)]
    [InlineData("axe.twinbit", 2, 3)]
    [InlineData("bow.verdant", 2, 4)]
    [InlineData("gear.travel-cloak", 3, 2)]
    public void Icon_IsFittedByFootprintIntoItsFootprintRect(string kind, int cols, int rows)
    {
        _scene.Items[9] = InventoryScene.Item(1, kind);
        _scene.Refresh();

        _scene.Frame();

        UiSprite sprite = _scene.Table.Get("item." + kind);
        Assert.Equal((cols, rows), (sprite.FootprintCols, sprite.FootprintRows));
        CanvasCall icon = _scene.Canvas.Named("item." + kind).Single();
        Assert.Equal(InventoryWindow.IconRect(sprite, _scene.ItemRect(9), _scene.Layout.Scale), icon.Rect);
        UiRect footprint = _scene.ItemRect(9);
        Assert.Equal(cols * 20f * _scene.Layout.Scale, footprint.W, 3);
        Assert.Equal(rows * 20f * _scene.Layout.Scale, footprint.H, 3);
        Assert.Equal(footprint.X + (footprint.W / 2f), icon.Rect.X + (icon.Rect.W / 2f), 3);
        Assert.Equal(footprint.Y + (footprint.H / 2f), icon.Rect.Y + (icon.Rect.H / 2f), 3);
    }

    [Fact]
    public void Icon_UsesThePageOfItsTier()
    {
        _scene.Items[0] = InventoryScene.Item(1, "gem.amber", rarity: ItemRarity.Common);
        _scene.Items[1] = InventoryScene.Item(2, "gear.iron-band", rarity: ItemRarity.Uncommon);
        _scene.Items[2] = InventoryScene.Item(3, "relic.disc", rarity: ItemRarity.Rare);
        _scene.Refresh();

        _scene.Frame();

        Assert.Equal(UiPage.ItemsCommon, _scene.Canvas.Named("item.gem.amber").Single().Sprite.Page);
        Assert.Equal(UiPage.ItemsUncommon, _scene.Canvas.Named("item.gear.iron-band").Single().Sprite.Page);
        Assert.Equal(UiPage.ItemsRare, _scene.Canvas.Named("item.relic.disc").Single().Sprite.Page);
    }

    [Theory]
    // slot, kind, expected fit. Wells: back is 3x2 (60x40 u), main hand 2x3 (40x60), neck 1x1, companion 2x2.
    [InlineData(EquipmentSlot.Back, "gear.travel-cloak", 1f)]
    [InlineData(EquipmentSlot.Back, "gear.gossamer-wings", 1f)]
    [InlineData(EquipmentSlot.MainHand, "gear.hunting-spear", 0.75f)]
    [InlineData(EquipmentSlot.MainHand, "axe.twinbit", 1f)]
    [InlineData(EquipmentSlot.OffHand, "bow.verdant", 0.75f)]
    [InlineData(EquipmentSlot.Companion, "gear.ember-wisp", 1f)]
    [InlineData(EquipmentSlot.Hands, "gear.iron-gauntlets", 1f)]
    [InlineData(EquipmentSlot.Feet, "gear.iron-boots", 1f)]
    [InlineData(EquipmentSlot.Legs, "gear.plated-leggings", 1f)]
    [InlineData(EquipmentSlot.Neck, "gear.ward-pendant", 1f)]
    [InlineData(EquipmentSlot.RingLeft, "gear.iron-band", 1f)]
    [InlineData(EquipmentSlot.RingRight, "gear.iron-band", 1f)]
    [InlineData(EquipmentSlot.Head, "gear.travel-cloak", 2f / 3f)]
    [InlineData(EquipmentSlot.Neck, "bow.verdant", 0.25f)]
    public void WornIcon_IsFittedByFootprintIntoItsWell(EquipmentSlot slot, string kind, float expectedFit)
    {
        _scene.Worn[(int)slot] = InventoryScene.Item(7, kind);
        _scene.Refresh();

        _scene.Frame();

        UiSprite sprite = _scene.Table.Get("item." + kind);
        UiRect well = _scene.Well((int)slot);
        CanvasCall icon = _scene.Canvas.Named("item." + kind).Single();
        Assert.Equal(InventoryWindow.IconRect(sprite, well, _scene.Layout.Scale), icon.Rect);
        float scale = _scene.Layout.Scale;
        Assert.Equal(sprite.W / 4f * expectedFit * scale, icon.Rect.W, 3);
        Assert.Equal(sprite.H / 4f * expectedFit * scale, icon.Rect.H, 3);
        Assert.Equal(well.X + (well.W / 2f), icon.Rect.X + (icon.Rect.W / 2f), 3);
        Assert.Equal(well.Y + (well.H / 2f), icon.Rect.Y + (icon.Rect.H / 2f), 3);
    }

    [Fact]
    public void ItemWithoutAnIcon_ShowsItsNameStartInTheMiddleOfItsFootprint()
    {
        _scene.Items[2] = InventoryScene.Item(7, "nothing.known");
        _scene.Refresh();

        _scene.Frame();

        // A prefix of the name, cut to the WIDTH of the cell rather than to a fixed three characters:
        // three glyphs at the size an 8 u label now takes need not fit a 20 u cell, and four often do.
        UiRect cell = _scene.ItemRect(2);
        CanvasCall label = Assert.Single(_scene.Canvas.Texts.Where(
            t => t.Color == ItemTierStyle.Body && t.Text.Length > 0 && "Item 7".StartsWith(t.Text, StringComparison.Ordinal)));
        Assert.True(label.Text.Length >= 3, label.Text);
        Assert.InRange(label.TextPos.X, cell.X, cell.Right);
        Assert.True(UiTextProbe.Width(_scene.Canvas, label) <= cell.W, $"'{label.Text}' overflows the cell");
    }

    [Fact]
    public void StackCount_IsDrawnOnlyAboveOne_AtTheFootprintsBottomRight()
    {
        _scene.Items[0] = InventoryScene.Item(1, "gem.amber", stack: 12);
        _scene.Items[1] = InventoryScene.Item(2, "gear.iron-band", stack: 1);
        _scene.Items[18] = InventoryScene.Item(3, "axe.twinbit", stack: 3);
        _scene.Refresh();

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("x12"));
        Assert.True(_scene.Canvas.HasText("x3"));
        Assert.False(_scene.Canvas.HasText("x1"));
        UiRect axe = _scene.ItemRect(18);
        CanvasCall count = _scene.Canvas.Texts.Last(t => t.Text == "x3");
        Assert.True(count.TextPos.X < axe.Right && count.TextPos.X > axe.X + (axe.W / 2f));
        Assert.True(count.TextPos.Y < axe.Bottom && count.TextPos.Y > axe.Y + (axe.H / 2f));
    }

    // ---- the window -------------------------------------------------------------------------

    [Theory]
    // slot, x, y, w, h (u), well sprite, silhouette (plan section 3A).
    [InlineData(EquipmentSlot.MainHand, 14f, 89f, 40f, 60f, "well.2x3", "sil.weapon.left")]
    [InlineData(EquipmentSlot.OffHand, 134f, 89f, 40f, 60f, "well.2x3", "sil.weapon.right")]
    [InlineData(EquipmentSlot.Head, 74f, 46f, 40f, 40f, "well.2x2", "sil.helm")]
    [InlineData(EquipmentSlot.Body, 74f, 89f, 40f, 60f, "well.2x3", "sil.armor")]
    [InlineData(EquipmentSlot.Hands, 14f, 152f, 40f, 40f, "well.2x2", "sil.gloves")]
    [InlineData(EquipmentSlot.Feet, 134f, 152f, 40f, 40f, "well.2x2", "sil.boots")]
    [InlineData(EquipmentSlot.Legs, 74f, 152f, 40f, 40f, "well.2x2", "sil.pants")]
    [InlineData(EquipmentSlot.Neck, 54f, 89f, 20f, 20f, "well.1x1", "sil.pendant")]
    [InlineData(EquipmentSlot.RingLeft, 54f, 152f, 20f, 20f, "well.1x1", "sil.ring")]
    [InlineData(EquipmentSlot.RingRight, 114f, 152f, 20f, 20f, "well.1x1", "sil.ring")]
    [InlineData(EquipmentSlot.Back, 114f, 46f, 60f, 40f, "well.3x2", "sil.wings")]
    [InlineData(EquipmentSlot.Companion, 14f, 46f, 40f, 40f, "well.2x2", "sil.pet")]
    public void Open_DrawsEachWellAtItsPlannedRectWithItsSilhouette(
        EquipmentSlot slot, float x, float y, float w, float h, string well, string silhouette)
    {
        _scene.Frame();

        UiRect expected = _scene.Layout.FromUnits(_scene.Layout.Inventory, x, y, w, h);
        Assert.Equal(expected, _scene.Well((int)slot));
        Assert.Contains(_scene.Canvas.Named(well), c => c.Rect.Equals(expected));
        Assert.Contains(_scene.Canvas.Named(silhouette), c => c.Rect.Equals(expected));
    }

    [Fact]
    public void Open_DrawsTwelveWells_ThreeSizesOfArtAndNoOthers()
    {
        _scene.Frame();

        Assert.Equal(3, _scene.Canvas.Named("well.2x3").Count());
        Assert.Equal(5, _scene.Canvas.Named("well.2x2").Count());
        Assert.Equal(3, _scene.Canvas.Named("well.1x1").Count());
        Assert.Single(_scene.Canvas.Named("well.3x2"));
        Assert.Equal(12, _scene.Canvas.Calls.Count(c => c.Name.StartsWith("well.", StringComparison.Ordinal)));
        Assert.Equal(12, _scene.Canvas.Calls.Count(c => c.Name.StartsWith("sil.", StringComparison.Ordinal)));
        Assert.Equal(2, _scene.Canvas.Named("sil.ring").Count());
    }

    [Fact]
    public void Open_BagIsAnEightByEightBoardOfTwentyUnitCells()
    {
        _scene.Frame();

        CanvasCall[] cells = _scene.Canvas.Named("cell.empty").ToArray();
        Assert.Equal(64, cells.Length);
        Assert.Empty(_scene.Canvas.Named("cell.occupied"));
        for (int n = 0; n < 64; n++)
        {
            Assert.Equal(_scene.Slot(n), cells[n].Rect);
            Assert.Equal(20f * _scene.Layout.Scale, cells[n].Rect.W, 3);
            Assert.Equal(20f * _scene.Layout.Scale, cells[n].Rect.H, 3);
        }

        UiRect board = InventoryWindow.BagRect(_scene.Layout, _scene.Layout.Inventory);
        Assert.Equal(_scene.Layout.FromUnits(_scene.Layout.Inventory, 15f, 200f, 160f, 160f), board);
        Assert.Equal(board.X, cells[0].Rect.X, 3);
        Assert.Equal(board.Right, cells[63].Rect.Right, 3);
        Assert.Equal(board.Bottom, cells[63].Rect.Bottom, 3);
    }

    [Fact]
    public void Open_AMultiCellItemTakesOutTheTilesUnderItsWholeFootprint()
    {
        _scene.Items[9] = InventoryScene.Item(10, "axe.twinbit");   // 2x3 from row 1, column 1
        _scene.Refresh();

        _scene.Frame();

        int[] covered = CellsOf(9, 2, 3).ToArray();
        Assert.Equal(new[] { 9, 10, 17, 18, 25, 26 }, covered);
        Assert.Equal(58, _scene.Canvas.Named("cell.empty").Count());
        Assert.Equal(6, _scene.Canvas.Named("cell.occupied").Count());
        Assert.Equal(CellRects(covered), RectsOf("cell.occupied"));
        Assert.Empty(CellRects(covered).Intersect(RectsOf("cell.empty")));
        Assert.All(_scene.Canvas.Named("cell.occupied"), c => Assert.Equal(CanvasCallKind.Tile, c.Kind));
    }

    [Fact]
    public void Open_HidesCurrencyRepairStoreArrowAndSetOption()
    {
        _scene.Items[0] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();

        _scene.Frame();

        foreach (CanvasCall call in _scene.Canvas.Calls.Where(c => c.Name.Length > 0))
        {
            Assert.DoesNotContain("currency", call.Name);
            Assert.DoesNotContain("repair", call.Name);
            Assert.DoesNotContain("store", call.Name);
            Assert.DoesNotContain("arrow", call.Name);
            Assert.DoesNotContain("setoption", call.Name);
            Assert.DoesNotContain("combine", call.Name.Replace("win.crown.combine", string.Empty).Replace("win.slab.combine", string.Empty).Replace("glyph.close", string.Empty));
        }
    }

    [Fact]
    public void Closed_DrawsNothingAndForgetsState()
    {
        _scene.View.InventoryOpen = false;

        _scene.Frame();

        Assert.Empty(_scene.Canvas.Calls);
    }

    [Fact]
    public void NoSnapshot_ShowsWaitingAndNoSlots()
    {
        _scene.View.Inventory = null;

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("Waiting for inventory..."));
        Assert.Empty(_scene.Canvas.Named("cell.empty"));
        Assert.Empty(_scene.Canvas.Named("well.2x3"));
    }

    // ---- worn items are not in the bag ------------------------------------------------------

    [Fact]
    public void WornItem_IsDrawnOnceInItsWell_AndLeavesTheBagUntouched()
    {
        _scene.Worn[2] = InventoryScene.Item(8, "helm.gilded", HeadMask);
        _scene.Refresh();

        _scene.Frame();

        CanvasCall icon = _scene.Canvas.Named("item.helm.gilded").Single();
        Assert.True(_scene.Well(2).Contains(InventoryScene.Centre(icon.Rect)));
        Assert.Equal(64, _scene.Canvas.Named("cell.empty").Count());
        Assert.Empty(_scene.Canvas.Named("cell.occupied"));
        Assert.Empty(_scene.Canvas.Named("cell.overlay.hover"));
    }

    [Fact]
    public void ABagRowMarkedEquipped_HasNoAliasTintAndNoOverlay()
    {
        _scene.Items[4] = InventoryScene.Item(5, "gem.amber");
        _scene.Equipped[4] = true;
        _scene.Refresh();

        _scene.Frame();

        CanvasCall icon = _scene.Canvas.Named("item.gem.amber").Single();
        Assert.Equal(255, icon.Color.A);
        Assert.Empty(_scene.Canvas.Named("cell.overlay.hover"));
        Assert.Empty(_scene.Canvas.Named("cell.overlay.invalid"));
    }

    // ---- held back --------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(16)]
    public void HeldBack_ShowsTheCountLineInTheFreeBandUnderTheBag(int count)
    {
        _scene.HeldBack = count;
        _scene.Refresh();

        _scene.Frame();

        string line = count == 1 ? "1 item held back: make room" : $"{count} items held back: make room";
        CanvasCall text = _scene.Canvas.Texts.Single(t => t.Text == line && t.Color == ItemTierStyle.Danger);
        UiRect band = _scene.Layout.FromUnits(_scene.Layout.Inventory, 15f, 364f, 160f, 8f);
        // The line is centred in the band now rather than hung off its top edge: the glyphs follow
        // ui_scale, so a top-aligned line of an 8 u band's text hangs out of the bottom of it.
        var t = UiText.For(_scene.Layout, InventoryWindow.BandTextUnits, InventoryWindow.BandUnits);
        Assert.Equal(band.X, text.TextPos.X, 3);
        Assert.Equal(UiText.TextY(band, t), text.TextPos.Y, 3);
        Assert.True(_scene.Canvas.Texts.Any(t => t.Text == line && t.Color == ItemTierStyle.Shadow));
    }

    [Fact]
    public void HeldBack_IsHiddenAtZero()
    {
        _scene.Frame();

        Assert.DoesNotContain(_scene.Canvas.Texts, t => t.Text.Contains("held back", StringComparison.Ordinal));
    }

    [Fact]
    public void HeldBackItems_AreNotDrawnInTheBag()
    {
        _scene.HeldBack = 4;
        _scene.Refresh();

        _scene.Frame();

        Assert.Equal(64, _scene.Canvas.Named("cell.empty").Count());
        Assert.Empty(_scene.Canvas.Named("item.gem.amber"));
    }

    // ---- input ------------------------------------------------------------------------------

    [Fact]
    public void LeftClick_DoesNothing()
    {
        _scene.Items[0] = InventoryScene.Item(1, "helm.gilded", HeadMask);
        _scene.Worn[1] = InventoryScene.Item(2, "gear.iron-blade", MainMask);
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(0));
        _scene.Input.LeftClick = true;
        _scene.Frame();
        _scene.Input.Pointer = InventoryScene.Centre(_scene.Well(1));
        _scene.Input.LeftClick = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
        Assert.Equal(0, _scene.Actions.Closes);
    }

    [Fact]
    public void CloseButton_ClosesOnce()
    {
        UiRect close = _scene.Layout.FromUnits(_scene.Layout.Inventory, 19f, 395f, 24f, 24f);
        _scene.Input.Pointer = InventoryScene.Centre(close);
        _scene.Input.LeftClick = true;

        _scene.Frame();

        Assert.Equal(1, _scene.Actions.Closes);
        Assert.Empty(_scene.Actions.Picked);
    }

    public static TheoryData<string, int, int> Footprints => new()
    {
        { "gem.amber", 1, 1 },
        { "feather.azure", 1, 2 },
        { "helm.gilded", 2, 2 },
        { "axe.twinbit", 2, 3 },
        { "bow.verdant", 2, 4 },
        { "gear.travel-cloak", 3, 2 },
    };

    [Theory]
    [MemberData(nameof(Footprints))]
    public void Hover_FromAnyCoveredCell_SelectsTheItemAndTintsEveryCellOfItsFootprint(string kind, int cols, int rows)
    {
        _scene.Items[9] = InventoryScene.Item(1, kind);
        _scene.Refresh();
        int[] covered = CellsOf(9, cols, rows).ToArray();

        foreach (int cell in covered)
        {
            _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(cell));
            _scene.Frame();

            InventoryUiState st = InventoryUiState.Current;
            Assert.True(st.HoverActive, $"cell {cell}");
            Assert.Equal(9, st.HoverSource);
            Assert.Equal(_scene.ItemRect(9), st.HoverRect);
            Assert.Equal(CellRects(covered), RectsOf("cell.hover"));
            Assert.Equal(covered.Length, _scene.Canvas.Named("cell.hover").Count());
            Assert.Empty(_scene.Canvas.Named("cell.occupied"));
        }
    }

    [Theory]
    [MemberData(nameof(Footprints))]
    public void Hover_JustOutsideTheFootprint_SelectsNothing(string kind, int cols, int rows)
    {
        _scene.Items[9] = InventoryScene.Item(1, kind);
        _scene.Refresh();

        // The cell left of the item, the cell above it, the cell past its right edge and the one under it.
        foreach (int cell in new[] { 8, 1, 9 + cols, 9 + (rows * 8) })
        {
            _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(cell));
            _scene.Frame();

            Assert.False(InventoryUiState.Current.HoverActive, $"cell {cell}");
            Assert.Empty(_scene.Canvas.Named("cell.hover"));
        }
    }

    [Theory]
    [MemberData(nameof(Footprints))]
    public void RightClick_FromAnyCoveredCell_OpensTheMenuOfTheAnchoredItem(string kind, int cols, int rows)
    {
        _scene.Items[9] = InventoryScene.Item(1, kind);
        _scene.Refresh();

        foreach (int cell in CellsOf(9, cols, rows))
        {
            _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(cell));
            _scene.Input.RightClick = true;
            _scene.Frame();

            InventoryUiState st = InventoryUiState.Current;
            Assert.True(st.MenuOpen, $"cell {cell}");
            Assert.Equal(9, st.MenuSource);
            Assert.Equal(1, st.MenuEntries.Count(e => e.RequiresConfirm));
            Assert.Equal(9, st.MenuEntries.Single(e => e.RequiresConfirm).Action.SlotIndex);
            st.CloseMenu();
            _scene.Frame();
        }
    }

    [Theory]
    [MemberData(nameof(Footprints))]
    public void Drag_FromAnyCoveredCell_PicksUpTheAnchoredItem(string kind, int cols, int rows)
    {
        _scene.Items[9] = InventoryScene.Item(1, kind);
        _scene.Refresh();

        foreach (int cell in CellsOf(9, cols, rows))
        {
            _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(cell));
            _scene.Input.StartDrag = true;
            _scene.Frame();

            InventoryUiState st = InventoryUiState.Current;
            Assert.True(st.DragActive, $"cell {cell}");
            Assert.Equal(9, st.DragSource);
            Assert.Equal(9, _scene.Input.ActiveDragPayload);

            // Let go on the window frame (not a target, not the world) so nothing is sent, and the
            // next cell starts clean.
            _scene.Input.Pointer = new Vector2(_scene.Layout.Inventory.X + 1f, _scene.Layout.Inventory.Y + 1f);
            _scene.Input.ReleaseDrag = true;
            _scene.Frame();
            Assert.False(st.DragActive);
        }

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void EmptyCells_AreNotWidgets()
    {
        _scene.Items[9] = InventoryScene.Item(1, "axe.twinbit");
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(40));
        _scene.Input.RightClick = true;
        _scene.Input.StartDrag = true;
        _scene.Frame();

        Assert.False(InventoryUiState.Current.MenuOpen);
        Assert.False(InventoryUiState.Current.DragActive);
        Assert.False(InventoryUiState.Current.HoverActive);
    }

    // ---- where a drop lands (decision D12) --------------------------------------------------

    [Fact]
    public void Drag_BagToFreeSpot_EmitsOneMoveToTheAnchorUnderTheGhost()
    {
        _scene.Items[0] = InventoryScene.Item(1, "axe.twinbit");   // 2x3
        _scene.Refresh();

        // The pointer is on the middle of a 2x3 footprint whose top-left cell is (row 3, col 3).
        Drag(InventoryScene.Centre(_scene.Slot(0)), DropPoint(27, 2, 3));

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 27), Assert.Single(_scene.Actions.Picked));
    }

    [Theory]
    [InlineData(-9.9f, 27)]
    [InlineData(0f, 27)]
    [InlineData(9.9f, 27)]
    [InlineData(10.1f, 28)]
    [InlineData(-10.1f, 26)]
    public void Drag_TheTargetIsTheCellNearestTheGhostsTopLeft(float offsetUnits, int expectedAnchor)
    {
        _scene.Items[0] = InventoryScene.Item(1, "axe.twinbit");
        _scene.Refresh();
        Vector2 point = DropPoint(27, 2, 3);
        UiRect unit = _scene.Layout.FromUnits(_scene.Layout.Inventory, 0f, 0f, 1f, 1f);

        Drag(InventoryScene.Centre(_scene.Slot(0)), point + new Vector2(offsetUnits * unit.W, 0f));

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, expectedAnchor), Assert.Single(_scene.Actions.Picked));
    }

    [Theory]
    // footprint, pointer at a corner of the board (cells from the top-left, just inside), expected anchor
    [InlineData(1, 1, 0.01f, 0.01f, 0)]
    [InlineData(1, 1, 7.99f, 7.99f, 63)]
    [InlineData(2, 3, 0.01f, 0.01f, 0)]
    [InlineData(2, 3, 7.99f, 7.99f, 46)]
    [InlineData(2, 3, 7.99f, 0.01f, 6)]
    [InlineData(2, 3, 0.01f, 7.99f, 40)]
    [InlineData(2, 4, 7.99f, 7.99f, 38)]
    [InlineData(3, 2, 7.99f, 7.99f, 53)]
    [InlineData(2, 2, 7.99f, 3.5f, 30)]
    [InlineData(2, 2, 3.5f, 7.99f, 51)]
    public void Drag_NearTheBoardEdge_ClampsTheAnchorSoTheWholeFootprintStaysOnTheBoard(
        int cols, int rows, float colCells, float rowCells, int expectedAnchor)
    {
        _scene.Footprints["test.item"] = F(cols, rows);
        _scene.Items[18] = InventoryScene.Item(1, "test.item");
        _scene.Refresh();

        Drag(InventoryScene.Centre(_scene.Slot(18)), BagPoint(colCells, rowCells));

        InventoryHudAction action = Assert.Single(_scene.Actions.Picked);
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 18, expectedAnchor), action);
    }

    [Fact]
    public void Drag_JustOutsideTheBoard_EmitsNothing()
    {
        _scene.Items[0] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();
        UiRect board = InventoryWindow.BagRect(_scene.Layout, _scene.Layout.Inventory);

        foreach (Vector2 outside in new[]
                 {
                     new Vector2(board.X - 1f, board.Y + 50f),
                     new Vector2(board.Right + 0.5f, board.Y + 50f),
                     new Vector2(board.X + 50f, board.Y - 1f),
                     new Vector2(board.X + 50f, board.Bottom + 0.5f),
                 })
        {
            Drag(InventoryScene.Centre(_scene.Slot(0)), outside);
        }

        Assert.Empty(_scene.Actions.Picked);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(3f, 2f)]
    public void TryDropAnchor_OfAOneByOne_IsTheCellUnderThePointer(float colCells, float rowCells)
    {
        UiLayout layout = _scene.Layout;
        Assert.True(InventoryWindow.TryDropAnchor(layout, layout.Inventory, BagPoint(colCells + 0.001f, rowCells + 0.001f), ItemFootprint.One, out int anchor));

        Assert.Equal((int)rowCells * 8 + (int)colCells, anchor);
    }

    [Fact]
    public void TryDropAnchor_RejectsAPointOffTheBoardAndAMalformedFootprint()
    {
        UiLayout layout = _scene.Layout;

        Assert.False(InventoryWindow.TryDropAnchor(layout, layout.Inventory, new Vector2(float.NaN, 5f), ItemFootprint.One, out _));
        Assert.False(InventoryWindow.TryDropAnchor(layout, layout.Inventory, new Vector2(1f, 1f), ItemFootprint.One, out _));
        Assert.False(InventoryWindow.TryDropAnchor(layout, layout.Inventory, BagPoint(2f, 2f), F(0, 1), out _));
        Assert.False(InventoryWindow.TryDropAnchor(layout, layout.Inventory, BagPoint(2f, 2f), F(5, 1), out _));
        Assert.False(InventoryWindow.TryDropAnchor(layout, layout.Inventory, BagPoint(2f, 2f), F(1, 9), out _));
    }

    // ---- bag to bag -------------------------------------------------------------------------

    [Fact]
    public void Drag_BagToBag_EmitsOneMove()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();

        Drag(_scene.Slot(1), _scene.Slot(10));

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 1, 10), Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_OntoExactlyOneOtherItem_IsASwapAndShowsValid()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Items[5] = InventoryScene.Item(2, "gear.iron-band");
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Slot(1)), InventoryScene.Centre(_scene.Slot(5)));
        Assert.Equal(new HashSet<UiRect> { _scene.Slot(5) }, RectsOf("cell.overlay.hover"));
        Assert.Empty(_scene.Canvas.Named("cell.overlay.invalid"));
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 1, 5), Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_OntoOneBiggerItem_IsASwapWhenTheOtherFindsARoom()
    {
        _scene.Items[0] = InventoryScene.Item(1, "gem.amber");
        _scene.Items[18] = InventoryScene.Item(2, "axe.twinbit");   // covers 18, 19, 26, 27, 34, 35
        _scene.Refresh();

        Drag(_scene.Slot(0), _scene.Slot(27));

        // The gem's footprint is 1x1 so it lands on cell 27, inside the axe: a swap.
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 27), Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_OntoTwoItems_ShowsInvalidOverTheWholeFootprintAndEmitsNothing()
    {
        _scene.Items[0] = InventoryScene.Item(1, "axe.twinbit");   // 2x3 at (row 0, col 0)
        _scene.Items[20] = InventoryScene.Item(2, "gem.amber");
        _scene.Items[36] = InventoryScene.Item(3, "gear.iron-band");
        _scene.Refresh();

        // Anchor 19 would cover 19, 20, 27, 28, 35, 36: two other items.
        Hold(InventoryScene.Centre(_scene.Slot(0)), DropPoint(19, 2, 3));
        Assert.Equal(CellRects(CellsOf(19, 2, 3)), RectsOf("cell.overlay.invalid"));
        Assert.Empty(_scene.Canvas.Named("cell.overlay.hover"));
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_OntoItsOwnAnchor_IsNotATarget()
    {
        _scene.Items[9] = InventoryScene.Item(1, "axe.twinbit");
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Slot(9)), DropPoint(9, 2, 3));
        Assert.Empty(_scene.Canvas.Named("cell.overlay.hover"));
        Assert.Empty(_scene.Canvas.Named("cell.overlay.invalid"));
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_AShortShiftInsideItsOwnCells_IsAMove()
    {
        _scene.Items[9] = InventoryScene.Item(1, "axe.twinbit");   // 9, 10, 17, 18, 25, 26
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Slot(9)), DropPoint(17, 2, 3));
        Assert.Equal(CellRects(CellsOf(17, 2, 3)), RectsOf("cell.overlay.hover"));
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 9, 17), Assert.Single(_scene.Actions.Picked));
    }

    [Theory]
    [MemberData(nameof(Footprints))]
    public void Drag_OverAFreeSpot_HighlightsExactlyTheFootprintCells(string kind, int cols, int rows)
    {
        _scene.Items[0] = InventoryScene.Item(1, kind);
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Slot(0)), DropPoint(27, cols, rows));

        int[] covered = CellsOf(27, cols, rows).ToArray();
        Assert.Equal(covered.Length, _scene.Canvas.Named("cell.overlay.hover").Count());
        Assert.Equal(CellRects(covered), RectsOf("cell.overlay.hover"));
        Assert.Empty(_scene.Canvas.Named("cell.overlay.invalid"));
    }

    [Theory]
    [MemberData(nameof(Footprints))]
    public void Drag_AtTheBottomRightCorner_KeepsEveryHighlightedCellInsideTheBoard(string kind, int cols, int rows)
    {
        _scene.Items[0] = InventoryScene.Item(1, kind);
        _scene.Refresh();
        UiRect board = InventoryWindow.BagRect(_scene.Layout, _scene.Layout.Inventory);

        Hold(InventoryScene.Centre(_scene.Slot(0)), BagPoint(7.99f, 7.99f));

        int anchor = ((8 - rows) * 8) + (8 - cols);
        Assert.Equal(CellRects(CellsOf(anchor, cols, rows)), RectsOf("cell.overlay.hover"));
        Assert.All(_scene.Canvas.Named("cell.overlay.hover"), c =>
        {
            Assert.True(c.Rect.X >= board.X - 0.001f && c.Rect.Right <= board.Right + 0.001f);
            Assert.True(c.Rect.Y >= board.Y - 0.001f && c.Rect.Bottom <= board.Bottom + 0.001f);
        });
    }

    [Fact]
    public void Drag_TheOverlayIsDrawnOverTheCellBacking()
    {
        _scene.Items[0] = InventoryScene.Item(1, "gem.amber");
        _scene.Items[5] = InventoryScene.Item(2, "gear.iron-band");
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Slot(0)), InventoryScene.Centre(_scene.Slot(5)));

        int overlay = _scene.Canvas.Calls.FindIndex(c => c.Name == "cell.overlay.hover");
        int backing = _scene.Canvas.Calls.FindIndex(c => c.Name == "cell.occupied" && c.Rect.Equals(_scene.Slot(5)));
        Assert.True(backing >= 0 && overlay > backing);
    }

    // ---- every verdict ----------------------------------------------------------------------

    [Fact]
    public void ResolveDrop_BagToBag_AgreesWithPlanMoveForEverySourceAndTarget()
    {
        _scene.Items[0] = InventoryScene.Item(1, "axe.twinbit");
        _scene.Items[2] = InventoryScene.Item(2, "gem.amber");
        _scene.Items[4] = InventoryScene.Item(3, "bow.verdant");
        _scene.Items[34] = InventoryScene.Item(4, "gear.travel-cloak");
        _scene.Items[30] = InventoryScene.Item(5, "helm.gilded");
        _scene.Items[33] = InventoryScene.Item(6, "feather.azure");
        _scene.Items[57] = InventoryScene.Item(7, "gear.iron-band");
        _scene.Items[63] = InventoryScene.Item(8, "relic.disc");
        _scene.Refresh();
        HudView v = _scene.View;
        int[] anchors = Enumerable.Range(0, 64).Where(a => InventoryWindow.IsPlaced(v, a)).ToArray();
        Assert.Equal(8, anchors.Length);

        int valid = 0;
        int invalid = 0;
        int none = 0;
        for (int source = 0; source < 64; source++)
        {
            for (int target = 0; target < 64; target++)
            {
                InventoryWindow.DropVerdict verdict = InventoryWindow.TryResolveDrop(v, source, target, out InventoryHudAction action);
                // A source that is not an item's anchor is no drag at all: the window answers None
                // where PlanMove, asked about it directly, would answer Blocked. A drop on the
                // item's own anchor is not skipped: that is PlanMove's Same, judged in the switch.
                if (!anchors.Contains(source))
                {
                    Assert.Equal(InventoryWindow.DropVerdict.None, verdict);
                    continue;
                }

                BagMovePlan plan = BagGrid.PlanMove(v.BagCells, v.BagFootprints, source, target);
                switch (plan.Verdict)
                {
                    case BagMoveVerdict.Blocked:
                        Assert.Equal(InventoryWindow.DropVerdict.Invalid, verdict);
                        Assert.Equal(default, action);
                        invalid++;
                        break;
                    case BagMoveVerdict.Same:
                        Assert.Equal(InventoryWindow.DropVerdict.None, verdict);
                        Assert.Equal(default, action);
                        none++;
                        break;
                    default:
                        Assert.Equal(InventoryWindow.DropVerdict.Valid, verdict);
                        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, source, target), action);
                        valid++;
                        break;
                }
            }
        }

        Assert.True(valid > 100 && invalid > 100, $"valid {valid}, invalid {invalid}, none {none}");
        // One Same per placed item: the drop on its own anchor.
        Assert.Equal(anchors.Length, none);
    }

    [Fact]
    public void ResolveDrop_ASwapThatLeavesTheOtherNowhereToGo_IsInvalid()
    {
        // A 1x1 gem dropped on the middle of a 2x3 axe on a full board: the axe cannot go anywhere.
        for (int n = 0; n < 64; n++)
        {
            _scene.Items[n] = InventoryScene.Item(100 + n, "gem.amber");
        }

        for (int n = 8; n < 8 + 24; n += 8)
        {
            _scene.Items[n] = null;
            _scene.Items[n + 1] = null;
        }

        _scene.Items[8] = InventoryScene.Item(1, "axe.twinbit");   // 8, 9, 16, 17, 24, 25
        _scene.Refresh();

        Assert.Equal(
            BagMoveVerdict.Blocked,
            BagGrid.PlanMove(_scene.View.BagCells, _scene.View.BagFootprints, 0, 17).Verdict);
        Assert.Equal(InventoryWindow.DropVerdict.Invalid, InventoryWindow.TryResolveDrop(_scene.View, 0, 17, out InventoryHudAction action));
        Assert.Equal(default, action);
    }

    [Fact]
    public void ResolveDrop_RejectsHostileIndices()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();

        foreach (int target in new[] { -5, 64, 99, 112, 200, int.MaxValue, int.MinValue })
        {
            Assert.Equal(InventoryWindow.DropVerdict.None, InventoryWindow.TryResolveDrop(_scene.View, 1, target, out _));
        }

        foreach (int source in new[] { -1, 64, 99, 112, 215, int.MaxValue, int.MinValue })
        {
            Assert.Equal(InventoryWindow.DropVerdict.None, InventoryWindow.TryResolveDrop(_scene.View, source, 3, out _));
        }

        // An anchor with no item, and a cell an item covers without being anchored there.
        _scene.Items[9] = InventoryScene.Item(2, "axe.twinbit");
        _scene.Refresh();
        Assert.Equal(InventoryWindow.DropVerdict.None, InventoryWindow.TryResolveDrop(_scene.View, 40, 3, out _));
        Assert.Equal(InventoryWindow.DropVerdict.None, InventoryWindow.TryResolveDrop(_scene.View, 10, 3, out _));
        Assert.Equal(InventoryWindow.DropVerdict.None, InventoryWindow.TryResolveDrop(_scene.View, 17, 3, out _));
    }

    // ---- bag to well ------------------------------------------------------------------------

    [Fact]
    public void Drag_BagToAllowedWell_EmitsOneEquipToAndShowsValidOnTheWell()
    {
        _scene.Items[3] = InventoryScene.Item(1, "helm.gilded", HeadMask);
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Slot(3)), InventoryScene.Centre(_scene.Well(2)));
        Assert.Contains(_scene.Canvas.Named("cell.overlay.hover"), c => c.Rect.Equals(_scene.Well(2)));
        Assert.Empty(_scene.Canvas.Named("cell.overlay.invalid"));
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.EquipTo, 3, EquipmentSlots.ToSlotIndex(EquipmentSlot.Head)),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_BagToWellThatDoesNotTakeIt_EmitsNothingAndShowsInvalid()
    {
        _scene.Items[3] = InventoryScene.Item(1, "helm.gilded", HeadMask);
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Slot(3)), InventoryScene.Centre(_scene.Well(3)));   // body
        Assert.Equal(_scene.Well(3), _scene.Canvas.Named("cell.overlay.invalid").Single().Rect);
        Assert.Empty(_scene.Canvas.Named("cell.overlay.hover").Where(c => c.Rect.Equals(_scene.Well(3))));
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Theory]
    [InlineData(EquipmentSlot.Hands)]
    [InlineData(EquipmentSlot.Feet)]
    [InlineData(EquipmentSlot.Legs)]
    [InlineData(EquipmentSlot.Neck)]
    [InlineData(EquipmentSlot.RingLeft)]
    [InlineData(EquipmentSlot.RingRight)]
    [InlineData(EquipmentSlot.Back)]
    [InlineData(EquipmentSlot.Companion)]
    public void Drag_BagToEachOfTheNewWells_EquipsWhenTheMaskAllows_AndNotOtherwise(EquipmentSlot slot)
    {
        _scene.Items[3] = InventoryScene.Item(1, "gem.amber", EquipmentSlots.MaskOf(slot));
        _scene.Items[4] = InventoryScene.Item(2, "gear.iron-band", HeadMask);
        _scene.Refresh();

        Drag(_scene.Slot(3), _scene.Well((int)slot));
        Drag(_scene.Slot(4), _scene.Well((int)slot));

        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.EquipTo, 3, EquipmentSlots.ToSlotIndex(slot)),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_BagOntoATakenWell_IsAValidSwapWhenTheWornItemCanComeBack()
    {
        _scene.Items[3] = InventoryScene.Item(1, "helm.gilded", HeadMask);
        _scene.Worn[2] = InventoryScene.Item(2, "armor.gilded", HeadMask);
        _scene.Refresh();

        Drag(_scene.Slot(3), _scene.Well(2));

        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.EquipTo, 3, EquipmentSlots.ToSlotIndex(EquipmentSlot.Head)),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_BagOntoATakenWell_IsInvalidWhenTheWornItemHasNoRoomInTheBag()
    {
        for (int n = 0; n < 64; n++)
        {
            _scene.Items[n] = InventoryScene.Item(100 + n, "gem.amber", n == 10 ? HeadMask : 0);
        }

        _scene.Worn[2] = InventoryScene.Item(2, "axe.twinbit", HeadMask);   // 2x3 cannot take a single freed cell
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Slot(10)), InventoryScene.Centre(_scene.Well(2)));
        Assert.Equal(_scene.Well(2), _scene.Canvas.Named("cell.overlay.invalid").Single().Rect);
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_BagOntoTheWellHoldingTheSameItem_IsNotATarget()
    {
        InventoryItemSnapshot helm = InventoryScene.Item(1, "helm.gilded", HeadMask);
        _scene.Items[3] = helm;
        _scene.Worn[2] = helm;
        _scene.Refresh();

        Drag(_scene.Slot(3), _scene.Well(2));

        Assert.Empty(_scene.Actions.Picked);
    }

    // ---- well to bag ------------------------------------------------------------------------

    [Fact]
    public void Drag_WellToBag_EmitsOneUnequipWithTheCellUnderTheGhost()
    {
        _scene.Worn[0] = InventoryScene.Item(1, "gear.iron-blade", MainMask);   // 2x3
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Well(0)), DropPoint(21, 2, 3));
        Assert.Equal(CellRects(CellsOf(21, 2, 3)), RectsOf("cell.overlay.hover"));
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.Unequip, EquipmentSlots.ToSlotIndex(EquipmentSlot.MainHand), 21),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_WellToACorner_ClampsTheFootprintOntoTheBoard()
    {
        _scene.Worn[0] = InventoryScene.Item(1, "gear.iron-blade", MainMask);

        _scene.Refresh();
        Drag(InventoryScene.Centre(_scene.Well(0)), BagPoint(7.99f, 7.99f));

        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.Unequip, EquipmentSlots.ToSlotIndex(EquipmentSlot.MainHand), 46),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_WellToABusyFootprint_ShowsInvalidAndEmitsNothing()
    {
        _scene.Worn[0] = InventoryScene.Item(1, "gear.iron-blade", MainMask);
        _scene.Items[30] = InventoryScene.Item(2, "gem.amber");
        _scene.Refresh();

        // Anchor 21 covers 21, 22, 29, 30, 37, 38: cell 30 is taken.
        Hold(InventoryScene.Centre(_scene.Well(0)), DropPoint(21, 2, 3));
        Assert.Equal(CellRects(CellsOf(21, 2, 3)), RectsOf("cell.overlay.invalid"));
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_WellToABagItemItWouldSwapWith_IsInvalid()
    {
        _scene.Worn[2] = InventoryScene.Item(1, "helm.gilded", HeadMask);
        _scene.Items[9] = InventoryScene.Item(2, "helm.gilded", HeadMask);
        _scene.Refresh();

        Drag(InventoryScene.Centre(_scene.Well(2)), DropPoint(9, 2, 2));

        Assert.Empty(_scene.Actions.Picked);
    }

    // ---- well to well -----------------------------------------------------------------------

    [Fact]
    public void Drag_RingToTheOtherRingWell_EmitsOneEquipToWellToWell()
    {
        _scene.Worn[(int)EquipmentSlot.RingLeft] = InventoryScene.Item(1, "gear.iron-band", RingMask);
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Well((int)EquipmentSlot.RingLeft)), InventoryScene.Centre(_scene.Well((int)EquipmentSlot.RingRight)));
        Assert.Contains(_scene.Canvas.Named("cell.overlay.hover"), c => c.Rect.Equals(_scene.Well((int)EquipmentSlot.RingRight)));
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Equal(
            new InventoryHudAction(
                InventoryHudActionKind.EquipTo,
                EquipmentSlots.ToSlotIndex(EquipmentSlot.RingLeft),
                EquipmentSlots.ToSlotIndex(EquipmentSlot.RingRight)),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_RingOntoATakenRingWell_IsASwap()
    {
        _scene.Worn[(int)EquipmentSlot.RingLeft] = InventoryScene.Item(1, "gear.iron-band", RingMask);
        _scene.Worn[(int)EquipmentSlot.RingRight] = InventoryScene.Item(2, "gear.iron-band", RingMask);
        _scene.Refresh();

        Drag(_scene.Well((int)EquipmentSlot.RingRight), _scene.Well((int)EquipmentSlot.RingLeft));

        Assert.Equal(
            new InventoryHudAction(
                InventoryHudActionKind.EquipTo,
                EquipmentSlots.ToSlotIndex(EquipmentSlot.RingRight),
                EquipmentSlots.ToSlotIndex(EquipmentSlot.RingLeft)),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_LanternBetweenTheHands_MovesWellToWell()
    {
        _scene.Worn[(int)EquipmentSlot.MainHand] = InventoryScene.Item(1, "gear.ward-lantern", HandsMask);
        _scene.Refresh();

        Drag(_scene.Well((int)EquipmentSlot.MainHand), _scene.Well((int)EquipmentSlot.OffHand));

        Assert.Equal(
            new InventoryHudAction(
                InventoryHudActionKind.EquipTo,
                EquipmentSlots.ToSlotIndex(EquipmentSlot.MainHand),
                EquipmentSlots.ToSlotIndex(EquipmentSlot.OffHand)),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_WellOntoATakenWell_WhoseItemCannotComeBack_ShowsInvalidAndEmitsNothing()
    {
        // The server refuses this swap (EquipFromWell): the blade may not sit in the off hand.
        _scene.Worn[(int)EquipmentSlot.OffHand] = InventoryScene.Item(1, "gear.ward-lantern", HandsMask);
        _scene.Worn[(int)EquipmentSlot.MainHand] = InventoryScene.Item(2, "gear.iron-blade", EquipmentSlots.MaskOf(EquipmentSlot.MainHand));
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Well((int)EquipmentSlot.OffHand)), InventoryScene.Centre(_scene.Well((int)EquipmentSlot.MainHand)));
        Assert.Equal(_scene.Well((int)EquipmentSlot.MainHand), _scene.Canvas.Named("cell.overlay.invalid").Single().Rect);
        Assert.Empty(_scene.Canvas.Named("cell.overlay.hover"));
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_WellOntoATakenWell_WhoseItemCanComeBack_IsAValidSwap()
    {
        _scene.Worn[(int)EquipmentSlot.OffHand] = InventoryScene.Item(1, "gear.ward-lantern", HandsMask);
        _scene.Worn[(int)EquipmentSlot.MainHand] = InventoryScene.Item(2, "gear.ward-lantern", HandsMask);
        _scene.Refresh();

        Drag(_scene.Well((int)EquipmentSlot.OffHand), _scene.Well((int)EquipmentSlot.MainHand));

        Assert.Equal(
            new InventoryHudAction(
                InventoryHudActionKind.EquipTo,
                EquipmentSlots.ToSlotIndex(EquipmentSlot.OffHand),
                EquipmentSlots.ToSlotIndex(EquipmentSlot.MainHand)),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_WellToAWellThatDoesNotTakeIt_ShowsInvalidAndEmitsNothing()
    {
        _scene.Worn[2] = InventoryScene.Item(1, "helm.gilded", HeadMask);
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Well(2)), InventoryScene.Centre(_scene.Well(0)));
        Assert.Equal(_scene.Well(0), _scene.Canvas.Named("cell.overlay.invalid").Single().Rect);
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_WellOntoItself_EmitsNothing()
    {
        _scene.Worn[2] = InventoryScene.Item(1, "helm.gilded", HeadMask);
        _scene.Refresh();

        Drag(_scene.Well(2), _scene.Well(2));

        Assert.Empty(_scene.Actions.Picked);
    }

    // ---- dragging, in general ---------------------------------------------------------------

    [Fact]
    public void Drag_DroppedOutsideTheTargets_EmitsNothing()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(1));
        _scene.Input.StartDrag = true;
        _scene.Frame();
        // On the window frame: neither a target nor the world. (Released in the world it is a Drop.)
        _scene.Input.Pointer = new Vector2(_scene.Layout.Inventory.X + 1f, _scene.Layout.Inventory.Y + 1f);
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_SnapshotChangeMidDrag_CancelsTheDropAndTheHighlight()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(1));
        _scene.Input.StartDrag = true;
        _scene.Frame();
        _scene.Refresh();   // a new snapshot arrives
        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(10));
        _scene.Frame();
        Assert.Empty(_scene.Canvas.Named("cell.overlay.hover"));
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_SnapshotChangeMidDrag_OfAMultiCellItem_CancelsTheDrop()
    {
        _scene.Items[1] = InventoryScene.Item(1, "axe.twinbit");
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Slot(1)), DropPoint(27, 2, 3));
        Assert.Equal(6, _scene.Canvas.Named("cell.overlay.hover").Count());
        _scene.Items[1] = null;
        _scene.Items[5] = InventoryScene.Item(1, "axe.twinbit");
        _scene.Refresh();
        _scene.Frame();
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_AfterACancelledDrag_ANewDragWorks()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();
        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(1));
        _scene.Input.StartDrag = true;
        _scene.Frame();
        _scene.Refresh();
        _scene.Frame();
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Drag(_scene.Slot(1), _scene.Slot(8));

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 1, 8), Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_WorksWhenTheInputCannotReportAPosition()
    {
        _scene.Input.ReportPointer = false;
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();

        Drag(_scene.Slot(1), _scene.Slot(2));

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 1, 2), Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_StartedOnAnEmptyCell_DoesNothing()
    {
        Drag(_scene.Slot(0), _scene.Slot(5));

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_WhileAMenuIsOpen_DoesNotStart()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();
        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(1));
        _scene.Input.RightClick = true;
        _scene.Frame();

        _scene.Input.StartDrag = true;
        _scene.Frame();

        Assert.Equal(-1, _scene.Input.ActiveDragPayload);
    }

    [Fact]
    public void Drag_TheItemInPlaceIsDimmedWhileItIsCarried()
    {
        _scene.Items[9] = InventoryScene.Item(1, "axe.twinbit");
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Slot(9)), new Vector2(400f, 300f));

        Assert.Equal(90, _scene.Canvas.Named("item.axe.twinbit").First().Color.A);
        Assert.Equal(6, _scene.Canvas.Named("cell.occupied").Count());
    }

    [Fact]
    public void Drag_TooltipIsNotShown()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();
        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(1));
        _scene.Frame();
        Assert.True(_scene.Canvas.HasText("Item 1"));

        _scene.Input.StartDrag = true;
        _scene.Frame();
        _scene.Frame();

        Assert.False(_scene.Canvas.HasText("Item 1"));
    }

    // ---- the hit tests ----------------------------------------------------------------------

    [Fact]
    public void HitTest_NamesTheCellOrTheWellUnderAPoint()
    {
        UiLayout layout = _scene.Layout;
        for (int n = 0; n < 64; n++)
        {
            Assert.Equal(n, InventoryWindow.HitTest(layout, layout.Inventory, InventoryScene.Centre(_scene.Slot(n))));
        }

        for (int w = 0; w < 12; w++)
        {
            Assert.Equal(100 + w, InventoryWindow.HitTest(layout, layout.Inventory, InventoryScene.Centre(_scene.Well(w))));
        }

        Assert.Equal(-1, InventoryWindow.HitTest(layout, layout.Inventory, new Vector2(1f, 1f)));
        Assert.Equal(-1, InventoryWindow.HitTest(layout, layout.Inventory, new Vector2(float.NaN, float.NaN)));
    }

    [Fact]
    public void TheWells_DoNotOverlapEachOtherOrTheBoard()
    {
        UiLayout layout = _scene.Layout;
        UiRect board = InventoryWindow.BagRect(layout, layout.Inventory);
        for (int a = 0; a < 12; a++)
        {
            UiRect ra = _scene.Well(a);
            Assert.True(ra.Bottom <= board.Y + 0.001f, $"well {a} reaches the board");
            for (int b = a + 1; b < 12; b++)
            {
                UiRect rb = _scene.Well(b);
                bool apart = ra.Right <= rb.X + 0.001f || rb.Right <= ra.X + 0.001f || ra.Bottom <= rb.Y + 0.001f || rb.Bottom <= ra.Y + 0.001f;
                Assert.True(apart, $"wells {a} and {b} overlap");
            }
        }
    }

    // ---- a hostile snapshot through the real builder ----------------------------------------

    [Fact]
    public void HostileSnapshot_OverlappingItems_FirstWinsAndTheLaterOneIsNeitherDrawnNorHit()
    {
        HudView view = new() { InventoryOpen = true, FootprintOf = _scene.FootprintOf };
        HudViewBuilder builder = new(_scene.Table, view);
        InventoryItemSnapshot axe = InventoryScene.Item(1, "axe.twinbit");     // 2x3 at 9: 9, 10, 17, 18, 25, 26
        InventoryItemSnapshot gem = InventoryScene.Item(2, "gem.amber");
        InventoryItemSnapshot bow = InventoryScene.Item(3, "bow.verdant");     // 2x4 at 18 would overlap the axe
        InventoryItemSnapshot offBoard = InventoryScene.Item(4, "gear.travel-cloak");  // 3x2 at 7 runs off the right edge
        PlayerInventorySnapshot hostile = new(
            "Tester",
            8,
            8,
            [
                new InventorySlotSnapshot(9, axe),
                new InventorySlotSnapshot(18, bow),
                new InventorySlotSnapshot(26, gem),
                new InventorySlotSnapshot(7, offBoard),
                new InventorySlotSnapshot(9, gem),
                new InventorySlotSnapshot(40, gem)
            ]);
        builder.Refresh(default, hostile);
        InventoryFakeInput input = new() { Pointer = InventoryScene.Centre(_scene.Slot(18)) };
        InventoryFakeCanvas canvas = new(_scene.Table);

        InventoryWindow.Build(canvas, input, view, _scene.Layout, _scene.Table, _scene.Actions);

        Assert.Single(canvas.Named("item.axe.twinbit"));
        Assert.Empty(canvas.Named("item.bow.verdant"));
        Assert.Empty(canvas.Named("item.gear.travel-cloak"));
        Assert.Single(canvas.Named("item.gem.amber"));
        Assert.Equal(6 + 1, canvas.Named("cell.occupied").Count() + canvas.Named("cell.hover").Count());
        Assert.Equal(64 - 7, canvas.Named("cell.empty").Count());
        // Cell 18 belongs to the axe, so the pointer there hovers the axe.
        Assert.True(InventoryUiState.Current.HoverActive);
        Assert.Equal(9, InventoryUiState.Current.HoverSource);
    }

    [Fact]
    public void ABagWithAnItemOnEveryCell_DrawsEveryCellAndEveryIcon()
    {
        for (int n = 0; n < 64; n++)
        {
            _scene.Items[n] = InventoryScene.Item(100 + n, n % 3 == 0 ? "gem.amber" : "gear.iron-band", rarity: (ItemRarity)(n % 3));
        }

        _scene.Refresh();
        _scene.Frame();

        Assert.Equal(64, _scene.Canvas.Named("cell.occupied").Count());
        Assert.Empty(_scene.Canvas.Named("cell.empty"));
        Assert.Equal(64, _scene.Canvas.Calls.Count(c => c.Name is "item.gem.amber" or "item.gear.iron-band"));
    }

    // ---- drop onto the ground ---------------------------------------------------------------

    private static readonly Vector2 WorldPoint = new(40f, 40f);

    private InventoryWindow.DropVerdict Release(int source, Vector2 p, out InventoryHudAction action) =>
        InventoryWindow.TryResolveRelease(_scene.Layout, _scene.Layout.Inventory, _scene.View, source, p, out action);

    [Fact]
    public void WorldPoint_IsClearOfTheBarAndBothWindows()
    {
        Assert.True(ClassicHudHitTest.OverWorld(_scene.Layout, true, WorldPoint));
        Assert.False(ClassicHudHitTest.OverWorld(_scene.Layout, true, InventoryScene.Centre(_scene.Layout.Bar)));
        Assert.False(ClassicHudHitTest.OverWorld(_scene.Layout, true, InventoryScene.Centre(_scene.Layout.Inventory)));
        Assert.False(ClassicHudHitTest.OverWorld(_scene.Layout, true, InventoryScene.Centre(_scene.Layout.Character)));
        Assert.False(ClassicHudHitTest.OverWorld(_scene.Layout, true, new Vector2(float.NaN, 40f)));
        Assert.False(ClassicHudHitTest.OverWorld(_scene.Layout, true, new Vector2(40f, float.PositiveInfinity)));
    }

    [Fact]
    public void TryResolveRelease_BagItemAtAWorldPoint_IsAValidDropNamingTheItem()
    {
        _scene.Items[9] = InventoryScene.Item(77, "gem.amber");
        _scene.Refresh();

        Assert.Equal(InventoryWindow.DropVerdict.Valid, Release(9, WorldPoint, out InventoryHudAction action));
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Drop, 9, 0, 77), action);
    }

    [Fact]
    public void TryResolveRelease_OverTheBar_TheWindowFrameOrTheCharacterWindow_IsNone()
    {
        _scene.Items[9] = InventoryScene.Item(77, "gem.amber");
        _scene.Refresh();
        UiRect win = _scene.Layout.Inventory;

        foreach (Vector2 p in new[]
                 {
                     InventoryScene.Centre(_scene.Layout.Bar),
                     new Vector2(win.X + 1f, win.Y + 1f),
                     InventoryScene.Centre(_scene.Layout.Character),
                     new Vector2(float.NaN, float.NaN),
                     new Vector2(float.PositiveInfinity, 40f)
                 })
        {
            Assert.Equal(InventoryWindow.DropVerdict.None, Release(9, p, out InventoryHudAction action));
            Assert.Equal(default, action);
        }
    }

    [Fact]
    public void TryResolveRelease_AWornItemAtAWorldPoint_IsInvalidAndEmitsNothing()
    {
        _scene.Worn[2] = InventoryScene.Item(5, "helm.gilded", HeadMask);
        _scene.Refresh();

        Assert.Equal(InventoryWindow.DropVerdict.Invalid, Release(EquipmentSlots.FirstSlotIndex + 2, WorldPoint, out InventoryHudAction action));
        Assert.Equal(default, action);
    }

    [Fact]
    public void TryResolveRelease_AnEmptySource_IsNone()
    {
        Assert.Equal(InventoryWindow.DropVerdict.None, Release(9, WorldPoint, out _));
    }

    [Fact]
    public void TryResolveRelease_AnInWindowTargetWinsOverTheWorld()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();

        Assert.Equal(InventoryWindow.DropVerdict.Valid, Release(1, InventoryScene.Centre(_scene.Slot(10)), out InventoryHudAction action));
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 1, 10), action);
    }

    [Fact]
    public void Drag_ReleasedInTheWorld_RecordsExactlyOneDrop()
    {
        _scene.Items[9] = InventoryScene.Item(77, "gem.amber");
        _scene.Refresh();

        Drag(InventoryScene.Centre(_scene.Slot(9)), WorldPoint);

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Drop, 9, 0, 77), Assert.Single(_scene.Actions.Picked));
        _scene.Frame();
        Assert.Single(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_OfAWornItemReleasedInTheWorld_RecordsNothing()
    {
        _scene.Worn[2] = InventoryScene.Item(5, "helm.gilded", HeadMask);
        _scene.Refresh();

        Drag(InventoryScene.Centre(_scene.Well(2)), WorldPoint);

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_ReleasedOverTheBarOrTheCharacterWindow_RecordsNothing()
    {
        _scene.Items[9] = InventoryScene.Item(77, "gem.amber");
        _scene.Refresh();

        Drag(InventoryScene.Centre(_scene.Slot(9)), InventoryScene.Centre(_scene.Layout.Bar));
        Drag(InventoryScene.Centre(_scene.Slot(9)), InventoryScene.Centre(_scene.Layout.Character));
        Drag(InventoryScene.Centre(_scene.Slot(9)), new Vector2(float.NaN, float.NaN));

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_CancelledByASnapshotChange_RecordsNoDrop()
    {
        _scene.Items[9] = InventoryScene.Item(77, "gem.amber");
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Slot(9)), WorldPoint);
        _scene.Refresh();   // a new snapshot while the item is held
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_WhenTheWindowClosesMidDrag_RecordsNoDrop()
    {
        _scene.Items[9] = InventoryScene.Item(77, "gem.amber");
        _scene.Refresh();

        Hold(InventoryScene.Centre(_scene.Slot(9)), WorldPoint);
        _scene.View.InventoryOpen = false;
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();
        _scene.View.InventoryOpen = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }
}
