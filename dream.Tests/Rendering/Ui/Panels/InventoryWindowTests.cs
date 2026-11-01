using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

public sealed class InventoryWindowTests : IDisposable
{
    private readonly InventoryScene _scene = new();

    private static readonly int HeadMask = EquipmentSlots.MaskOf(EquipmentSlot.Head);
    private static readonly int MainMask = EquipmentSlots.MaskOf(EquipmentSlot.MainHand);

    public void Dispose() => _scene.Dispose();

    // Pick up from one rect, move, release on another. Two frames.
    private void Drag(UiRect from, UiRect to)
    {
        _scene.Input.Pointer = InventoryScene.Centre(from);
        _scene.Input.StartDrag = true;
        _scene.Frame();
        _scene.Input.Pointer = InventoryScene.Centre(to);
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();
    }

    [Theory]
    [InlineData(1, 1, 40f, 40f, 1f)]
    [InlineData(1, 2, 40f, 40f, 1f)]
    [InlineData(2, 2, 40f, 40f, 1f)]
    [InlineData(2, 3, 40f, 40f, 2f / 3f)]
    [InlineData(2, 4, 40f, 40f, 0.5f)]
    [InlineData(3, 2, 40f, 40f, 2f / 3f)]
    [InlineData(2, 3, 40f, 60f, 1f)]
    [InlineData(0, 0, 40f, 40f, 1f)]
    public void FitScale_FitsTheFootprintAndNeverEnlarges(int cols, int rows, float boxW, float boxH, float expected)
    {
        Assert.Equal(expected, InventoryWindow.FitScale(cols, rows, boxW, boxH), 4);
    }

    [Fact]
    public void IconRect_IsCentredAndScaledByFootprint()
    {
        UiSprite axe = _scene.Table.Get("item.axe.twinbit");   // 208x288 px, footprint 2x3
        UiRect box = _scene.Slot(5);

        UiRect r = InventoryWindow.IconRect(axe, box, _scene.Layout.Scale);

        float fit = 2f / 3f;
        Assert.Equal(208f / 4f * fit * _scene.Layout.Scale, r.W, 3);
        Assert.Equal(288f / 4f * fit * _scene.Layout.Scale, r.H, 3);
        Assert.Equal(box.X + (box.W / 2f), r.X + (r.W / 2f), 3);
        Assert.Equal(box.Y + (box.H / 2f), r.Y + (r.H / 2f), 3);
    }

    [Fact]
    public void Open_DrawsFourWellsEachWithItsSilhouette()
    {
        _scene.Frame();

        Assert.Equal(3, _scene.Canvas.Named("well.2x3").Count());
        Assert.Single(_scene.Canvas.Named("well.2x2"));
        Assert.Single(_scene.Canvas.Named("sil.weapon.left"));
        Assert.Single(_scene.Canvas.Named("sil.weapon.right"));
        Assert.Single(_scene.Canvas.Named("sil.helm"));
        Assert.Single(_scene.Canvas.Named("sil.armor"));
        Assert.Empty(_scene.Canvas.Named("well.1x1"));
        Assert.Empty(_scene.Canvas.Named("well.3x2"));
        Assert.Empty(_scene.Canvas.Named("sil.boots"));
        Assert.Empty(_scene.Canvas.Named("sil.pet"));
    }

    [Fact]
    public void Open_BagIsFourByFourSlotsOfFourCells()
    {
        _scene.Items[3] = InventoryScene.Item(10, "gem.amber");
        _scene.Refresh();

        _scene.Frame();

        // 15 empty slots x 4 cells, one occupied slot tiled once.
        Assert.Equal(60, _scene.Canvas.Named("cell.empty").Count());
        Assert.Single(_scene.Canvas.Named("cell.occupied"));
        UiRect slot3 = _scene.Slot(3);
        Assert.Equal(40f * _scene.Layout.Scale, slot3.W, 3);
        Assert.Equal(slot3, _scene.Canvas.Named("cell.occupied").Single().Rect);
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

    [Fact]
    public void Icon_UsesThePageOfItsTier()
    {
        _scene.Items[0] = InventoryScene.Item(1, "gem.amber", rarity: ItemRarity.Common);
        _scene.Items[1] = InventoryScene.Item(2, "ring.ice", rarity: ItemRarity.Uncommon);
        _scene.Items[2] = InventoryScene.Item(3, "relic.disc", rarity: ItemRarity.Rare);
        _scene.Refresh();

        _scene.Frame();

        Assert.Equal(UiPage.ItemsCommon, _scene.Canvas.Named("item.gem.amber").Single().Sprite.Page);
        Assert.Equal(UiPage.ItemsUncommon, _scene.Canvas.Named("item.ring.ice").Single().Sprite.Page);
        Assert.Equal(UiPage.ItemsRare, _scene.Canvas.Named("item.relic.disc").Single().Sprite.Page);
    }

    [Fact]
    public void Icon_IsFittedByFootprintIntoTheSlot()
    {
        _scene.Items[6] = InventoryScene.Item(1, "bow.verdant");   // 2x4
        _scene.Refresh();

        _scene.Frame();

        CanvasCall icon = _scene.Canvas.Named("item.bow.verdant").Single();
        UiSprite bow = _scene.Table.Get("item.bow.verdant");
        Assert.Equal(InventoryWindow.IconRect(bow, _scene.Slot(6), _scene.Layout.Scale), icon.Rect);
        Assert.Equal(0.5f, InventoryWindow.FitScale(bow.FootprintCols, bow.FootprintRows, 40f, 40f), 4);
    }

    [Fact]
    public void ItemWithoutAnIcon_ShowsItsNameStart()
    {
        _scene.Items[2] = InventoryScene.Item(7, "nothing.known");
        _scene.Refresh();

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("Ite"));
    }

    [Fact]
    public void StackCount_IsDrawnOnlyAboveOne()
    {
        _scene.Items[0] = InventoryScene.Item(1, "gem.amber", stack: 12);
        _scene.Items[1] = InventoryScene.Item(2, "ring.ice", stack: 1);
        _scene.Refresh();

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("x12"));
        Assert.False(_scene.Canvas.HasText("x1"));
    }

    [Fact]
    public void EquippedItem_ShowsInWellAndInBagAt55PercentWithOverlay()
    {
        InventoryItemSnapshot blade = InventoryScene.Item(5, "gear.iron-blade", MainMask);
        _scene.Items[4] = blade;
        _scene.Equipped[4] = true;
        _scene.Worn[0] = blade;
        _scene.Refresh();

        _scene.Frame();

        CanvasCall[] icons = _scene.Canvas.Named("item.gear.iron-blade").ToArray();
        Assert.Equal(2, icons.Length);
        Assert.Contains(icons, c => c.Color.A == 255);
        Assert.Contains(icons, c => c.Color.A == 140);
        CanvasCall overlay = _scene.Canvas.Named("cell.overlay.hover").Single();
        Assert.Equal(_scene.Slot(4), overlay.Rect);
    }

    [Fact]
    public void LegacyEquippedItemWithNoBagRow_ShowsInTheWellOnly()
    {
        _scene.Worn[2] = InventoryScene.Item(8, "helm.gilded", HeadMask);
        _scene.Refresh();

        _scene.Frame();

        CanvasCall icon = _scene.Canvas.Named("item.helm.gilded").Single();
        UiRect well = _scene.Well(2);
        Assert.True(well.Contains(InventoryScene.Centre(icon.Rect)));
        Assert.Equal(64, _scene.Canvas.Named("cell.empty").Count());
        Assert.Empty(_scene.Canvas.Named("cell.overlay.hover"));
    }

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

    [Fact]
    public void Drag_BagToBag_EmitsOneMove()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();

        Drag(_scene.Slot(1), _scene.Slot(10));

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 1, 10), Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_BagToOccupiedBag_EmitsOneMoveForTheSwap()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Items[2] = InventoryScene.Item(2, "ring.ice");
        _scene.Refresh();

        Drag(_scene.Slot(1), _scene.Slot(2));

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 1, 2), Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_BagToItsOwnSlot_EmitsNothing()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();

        Drag(_scene.Slot(1), _scene.Slot(1));

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_BagToAllowedWell_EmitsOneEquipTo()
    {
        _scene.Items[3] = InventoryScene.Item(1, "helm.gilded", HeadMask);
        _scene.Refresh();

        Drag(_scene.Slot(3), _scene.Well(2));

        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.EquipTo, 3, EquipmentSlots.ToSlotIndex(EquipmentSlot.Head)),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_BagToWellThatDoesNotTakeIt_EmitsNothingAndShowsInvalid()
    {
        _scene.Items[3] = InventoryScene.Item(1, "helm.gilded", HeadMask);
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(3));
        _scene.Input.StartDrag = true;
        _scene.Frame();
        _scene.Input.Pointer = InventoryScene.Centre(_scene.Well(3));   // body
        _scene.Frame();
        Assert.Equal(_scene.Well(3), _scene.Canvas.Named("cell.overlay.invalid").Single().Rect);
        Assert.Empty(_scene.Canvas.Named("cell.overlay.hover").Where(c => c.Rect.Equals(_scene.Well(3))));
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_ValidTargetShowsTheHoverOverlay()
    {
        _scene.Items[3] = InventoryScene.Item(1, "helm.gilded", HeadMask);
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(3));
        _scene.Input.StartDrag = true;
        _scene.Frame();
        _scene.Input.Pointer = InventoryScene.Centre(_scene.Well(2));
        _scene.Frame();

        Assert.Contains(_scene.Canvas.Named("cell.overlay.hover"), c => c.Rect.Equals(_scene.Well(2)));
        Assert.Empty(_scene.Canvas.Named("cell.overlay.invalid"));
    }

    [Fact]
    public void Drag_WellToBag_EmitsOneUnequip()
    {
        _scene.Worn[2] = InventoryScene.Item(1, "helm.gilded", HeadMask);
        _scene.Refresh();

        Drag(_scene.Well(2), _scene.Slot(9));

        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.Unequip, EquipmentSlots.ToSlotIndex(EquipmentSlot.Head)),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Drag_WellToWell_EmitsNothing()
    {
        _scene.Worn[2] = InventoryScene.Item(1, "helm.gilded", HeadMask | MainMask);
        _scene.Refresh();

        Drag(_scene.Well(2), _scene.Well(0));

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_DroppedOutsideTheTargets_EmitsNothing()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(1));
        _scene.Input.StartDrag = true;
        _scene.Frame();
        _scene.Input.Pointer = new OpenTK.Mathematics.Vector2(10f, 10f);
        _scene.Input.ReleaseDrag = true;
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_SnapshotChangeMidDrag_CancelsTheDrop()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(1));
        _scene.Input.StartDrag = true;
        _scene.Frame();
        _scene.Refresh();   // a new snapshot arrives
        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(10));
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
    public void Drag_StartedOnAnEmptySlot_DoesNothing()
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
    public void ResolveDrop_RejectsHostileIndices()
    {
        _scene.Items[1] = InventoryScene.Item(1, "gem.amber");
        _scene.Refresh();

        foreach (int target in new[] { -5, 16, 99, 112, int.MaxValue, int.MinValue })
        {
            Assert.Equal(InventoryWindow.DropVerdict.None, InventoryWindow.TryResolveDrop(_scene.View, 1, target, out _));
        }

        foreach (int source in new[] { -1, 16, 99, 112, int.MaxValue })
        {
            Assert.Equal(InventoryWindow.DropVerdict.None, InventoryWindow.TryResolveDrop(_scene.View, source, 3, out _));
        }
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
}
