using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

public sealed class ItemOverlaysTests : IDisposable
{
    private readonly InventoryScene _scene = new();

    private static readonly int HeadMask = EquipmentSlots.MaskOf(EquipmentSlot.Head);
    private static readonly int MainMask = EquipmentSlots.MaskOf(EquipmentSlot.MainHand);

    public void Dispose() => _scene.Dispose();

    private void RightClickSlot(int slot)
    {
        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(slot));
        _scene.Input.RightClick = true;
        _scene.Frame();
    }

    private void ClickMenuEntry(string label)
    {
        CanvasCall text = _scene.Canvas.Texts.Single(t => t.Text == label);
        // Entry text sits inside its row, so a click on the text hits the row.
        _scene.Input.Pointer = new Vector2(text.TextPos.X + 2f, text.TextPos.Y + 2f);
        _scene.Input.LeftClick = true;
        _scene.Frame();
    }

    private IEnumerable<string> MenuLabels() => _scene.Canvas.Texts.Select(t => t.Text);

    // ---- menu and confirm -------------------------------------------------------------------

    [Fact]
    public void RightClick_OnABagItem_OpensItsMenuAndHoldsModal()
    {
        _scene.Items[2] = InventoryScene.Item(11, "helm.gilded", HeadMask);
        _scene.Refresh();

        RightClickSlot(2);

        Assert.True(_scene.Canvas.HasText("Equip: Head"));
        Assert.True(_scene.Canvas.HasText("Destroy..."));
        Assert.False(_scene.Canvas.HasText("Unequip"));
        Assert.True(_scene.Input.ModalActive);
        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Menu_EquipEntry_EmitsOneEquipToAndCloses()
    {
        _scene.Items[2] = InventoryScene.Item(11, "helm.gilded", HeadMask);
        _scene.Refresh();
        RightClickSlot(2);

        ClickMenuEntry("Equip: Head");

        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.EquipTo, 2, EquipmentSlots.ToSlotIndex(EquipmentSlot.Head)),
            Assert.Single(_scene.Actions.Picked));
        _scene.Frame();
        Assert.False(_scene.Input.ModalActive);
        Assert.False(_scene.Canvas.HasText("Equip: Head"));
    }

    [Fact]
    public void Destroy_NeedsAConfirmAndCarriesTheItemInstanceId()
    {
        _scene.Items[2] = InventoryScene.Item(4242, "helm.gilded", HeadMask);
        _scene.Refresh();
        RightClickSlot(2);

        ClickMenuEntry("Destroy...");

        Assert.Empty(_scene.Actions.Picked);
        Assert.True(_scene.Canvas.HasText("Destroy Item 4242?"));
        Assert.True(_scene.Canvas.HasText("This cannot be undone."));
        Assert.True(_scene.Input.ModalActive);

        CanvasCall confirm = _scene.Canvas.Texts.First(t => t.Text == "Destroy");
        _scene.Input.Pointer = new Vector2(confirm.TextPos.X + 2f, confirm.TextPos.Y + 2f);
        _scene.Input.LeftClick = true;
        _scene.Frame();

        InventoryHudAction action = Assert.Single(_scene.Actions.Picked);
        Assert.Equal(InventoryHudActionKind.Destroy, action.Kind);
        Assert.Equal(2, action.SlotIndex);
        Assert.Equal(4242L, action.ItemInstanceId);
        _scene.Frame();
        Assert.False(_scene.Input.ModalActive);
    }

    [Fact]
    public void Destroy_CancelEmitsNothing()
    {
        _scene.Items[2] = InventoryScene.Item(4242, "helm.gilded", HeadMask);
        _scene.Refresh();
        RightClickSlot(2);
        ClickMenuEntry("Destroy...");

        CanvasCall cancel = _scene.Canvas.Texts.First(t => t.Text == "Cancel");
        _scene.Input.Pointer = new Vector2(cancel.TextPos.X + 2f, cancel.TextPos.Y + 2f);
        _scene.Input.LeftClick = true;
        _scene.Frame();
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
        Assert.False(_scene.Canvas.HasText("This cannot be undone."));
    }

    [Fact]
    public void Destroy_ClickOutsideTheConfirmCancels()
    {
        _scene.Items[2] = InventoryScene.Item(4242, "helm.gilded", HeadMask);
        _scene.Refresh();
        RightClickSlot(2);
        ClickMenuEntry("Destroy...");

        _scene.Input.Pointer = new Vector2(5f, 5f);
        _scene.Input.LeftClick = true;
        _scene.Frame();
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
        Assert.False(_scene.Canvas.HasText("This cannot be undone."));
    }

    [Fact]
    public void Destroy_ConfirmClosesWhenItsItemLeavesTheSlot()
    {
        _scene.Items[2] = InventoryScene.Item(4242, "helm.gilded", HeadMask);
        _scene.Refresh();
        RightClickSlot(2);
        ClickMenuEntry("Destroy...");
        Assert.True(_scene.Canvas.HasText("This cannot be undone."));

        _scene.Items[2] = InventoryScene.Item(5555, "ring.ice");
        _scene.Refresh();
        _scene.Frame();

        Assert.False(_scene.Canvas.HasText("This cannot be undone."));
        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Destroy_ConfirmSurvivesAnUnrelatedSnapshot()
    {
        _scene.Items[2] = InventoryScene.Item(4242, "helm.gilded", HeadMask);
        _scene.Refresh();
        RightClickSlot(2);
        ClickMenuEntry("Destroy...");

        _scene.Items[9] = InventoryScene.Item(77, "ring.ice");
        _scene.Refresh();
        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("This cannot be undone."));
    }

    [Fact]
    public void EquippedBagItem_OffersUnequipAndNoDestroy()
    {
        InventoryItemSnapshot blade = InventoryScene.Item(5, "gear.iron-blade", MainMask);
        _scene.Items[4] = blade;
        _scene.Equipped[4] = true;
        _scene.Worn[0] = blade;
        _scene.Refresh();

        RightClickSlot(4);

        Assert.True(_scene.Canvas.HasText("Unequip"));
        Assert.True(_scene.Canvas.HasText("Equip: Main Hand"));
        Assert.DoesNotContain(MenuLabels(), t => t.StartsWith("Destroy", StringComparison.Ordinal));
    }

    [Fact]
    public void EquippedBagItem_UnequipEntryEmitsOneUnequip()
    {
        InventoryItemSnapshot blade = InventoryScene.Item(5, "gear.iron-blade", MainMask);
        _scene.Items[4] = blade;
        _scene.Equipped[4] = true;
        _scene.Worn[0] = blade;
        _scene.Refresh();
        RightClickSlot(4);

        ClickMenuEntry("Unequip");

        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.Unequip, EquipmentSlots.ToSlotIndex(EquipmentSlot.MainHand)),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void RightClick_OnAWell_OffersOnlyUnequip()
    {
        _scene.Worn[2] = InventoryScene.Item(9, "helm.gilded", HeadMask);
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Well(2));
        _scene.Input.RightClick = true;
        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("Unequip"));
        Assert.False(_scene.Canvas.HasText("Destroy..."));
        ClickMenuEntry("Unequip");
        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.Unequip, EquipmentSlots.ToSlotIndex(EquipmentSlot.Head)),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void RightClick_OnAnEmptySlot_OpensNothing()
    {
        RightClickSlot(7);

        Assert.False(_scene.Input.ModalActive);
        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Menu_ClickElsewhereClosesWithoutAnAction()
    {
        _scene.Items[2] = InventoryScene.Item(11, "helm.gilded", HeadMask);
        _scene.Refresh();
        RightClickSlot(2);

        _scene.Input.Pointer = new Vector2(5f, 5f);
        _scene.Input.LeftClick = true;
        _scene.Frame();
        _scene.Frame();

        Assert.Empty(_scene.Actions.Picked);
        Assert.False(_scene.Canvas.HasText("Equip: Head"));
        Assert.False(_scene.Input.ModalActive);
    }

    [Fact]
    public void Menu_ClosesOnASnapshotChange()
    {
        _scene.Items[2] = InventoryScene.Item(11, "helm.gilded", HeadMask);
        _scene.Refresh();
        RightClickSlot(2);

        _scene.Refresh();
        _scene.Frame();

        Assert.False(_scene.Canvas.HasText("Equip: Head"));
        Assert.False(_scene.Input.ModalActive);
    }

    [Fact]
    public void Menu_RightClickOnAnotherSlot_ClosesItAndOpensNoSecondMenu()
    {
        _scene.Items[2] = InventoryScene.Item(11, "helm.gilded", HeadMask);
        _scene.Items[3] = InventoryScene.Item(12, "ring.ice");
        _scene.Refresh();
        RightClickSlot(2);

        RightClickSlot(3);
        _scene.Frame();

        Assert.False(_scene.Canvas.HasText("Destroy..."));
        Assert.False(_scene.Input.ModalActive);
        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Closing_TheInventory_ReleasesModalAndForgetsTheMenu()
    {
        _scene.Items[2] = InventoryScene.Item(11, "helm.gilded", HeadMask);
        _scene.Refresh();
        RightClickSlot(2);
        Assert.True(_scene.Input.ModalActive);

        _scene.View.InventoryOpen = false;
        _scene.Frame();

        Assert.False(_scene.Input.ModalActive);
        Assert.Empty(_scene.Canvas.Calls);
        _scene.View.InventoryOpen = true;
        _scene.Frame();
        Assert.False(_scene.Canvas.HasText("Equip: Head"));
    }

    // ---- tooltip ----------------------------------------------------------------------------

    [Fact]
    public void Tooltip_IsAboveTheItemWithHalfACellOverlap()
    {
        UiRect footprint = new(500f, 300f, 60f, 60f);
        float scale = 3f;

        Vector2 p = ItemOverlays.PlaceTooltip(footprint, 120f, 80f, scale, 1280f, 720f);

        Assert.Equal(footprint.X + 30f - 60f, p.X, 3);
        Assert.Equal(footprint.Y + (10f * scale) - 80f, p.Y, 3);   // bottom = footprint top + 0.5 cell
    }

    [Fact]
    public void Tooltip_FlipsBelowWhenItWouldLeaveTheTop()
    {
        UiRect footprint = new(500f, 20f, 60f, 60f);

        Vector2 p = ItemOverlays.PlaceTooltip(footprint, 120f, 80f, 3f, 1280f, 720f);

        Assert.Equal(footprint.Bottom, p.Y, 3);
    }

    [Fact]
    public void Tooltip_IsClampedToTheScreen()
    {
        Vector2 left = ItemOverlays.PlaceTooltip(new UiRect(2f, 300f, 60f, 60f), 200f, 80f, 3f, 1280f, 720f);
        Vector2 right = ItemOverlays.PlaceTooltip(new UiRect(1240f, 300f, 60f, 60f), 200f, 80f, 3f, 1280f, 720f);
        Vector2 bottom = ItemOverlays.PlaceTooltip(new UiRect(500f, 20f, 60f, 60f), 200f, 700f, 3f, 1280f, 720f);

        Assert.Equal(0f, left.X);
        Assert.Equal(1280f - 200f, right.X);
        Assert.Equal(720f - 700f, bottom.Y);
    }

    [Fact]
    public void Tooltip_ShowsOnHoverWithNameInTierColour()
    {
        _scene.Items[5] = InventoryScene.Item(31, "ring.ice", rarity: ItemRarity.Rare) with { IsRolled = true };
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(5));
        _scene.Frame();

        CanvasCall name = _scene.Canvas.Texts.Where(t => t.Text == "Item 31").Last();
        Assert.Equal(ItemTierStyle.Rare, name.Color);
        Assert.True(_scene.Canvas.HasText("A plain item."));
        Assert.True(_scene.Canvas.HasText("Detail one"));
        Assert.True(_scene.Canvas.HasText("Rare"));
        Assert.Single(_scene.Canvas.Named("tip.panel"));
    }

    [Fact]
    public void Tooltip_ListsRolledModifiersInTheBonusColour()
    {
        InventoryItemSnapshot rolled = InventoryScene.Item(31, "ring.ice", rarity: ItemRarity.Uncommon) with
        {
            IsRolled = true,
            Modifiers = [new StatModifier(StatId.Strength, StatOp.Flat, 3f)]
        };
        _scene.Items[5] = rolled;
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(5));
        _scene.Frame();

        CanvasCall bonus = _scene.Canvas.Texts.Last(t => t.Text == "+3 Strength");
        Assert.Equal(ItemTierStyle.Bonus, bonus.Color);
    }

    [Fact]
    public void Tooltip_ShowsTheStackCount()
    {
        _scene.Items[5] = InventoryScene.Item(31, "ring.ice", stack: 7);
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(5));
        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("Count: 7"));
    }

    [Fact]
    public void Tooltip_ServerTextIsLiteralAndWrapped()
    {
        InventoryItemSnapshot hostile = InventoryScene.Item(31, "ring.ice") with
        {
            DisplayName = "100% {0} %s",
            Description = new string('w', 300) + " tail %d",
            DetailLines = ["%n %n %n", null!]
        };
        _scene.Items[5] = hostile;
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(5));
        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("100% {0} %s"));
        Assert.True(_scene.Canvas.HasText("%n %n %n"));
        float maxWidth = 140f * _scene.Layout.Scale;
        CanvasCall[] wrapped = _scene.Canvas.Texts.Where(t => t.Text.StartsWith("www", StringComparison.Ordinal) && t.Color != ItemTierStyle.Shadow).ToArray();
        Assert.True(wrapped.Length >= 2);
        Assert.All(wrapped, t => Assert.True(t.Text.Length * _scene.Canvas.CharWidth <= maxWidth, t.Text));
    }

    [Fact]
    public void Tooltip_HiddenWhileAMenuIsOpen()
    {
        _scene.Items[2] = InventoryScene.Item(11, "helm.gilded", HeadMask);
        _scene.Refresh();
        RightClickSlot(2);

        _scene.Frame();

        Assert.False(_scene.Canvas.HasText("Item 11"));
        Assert.Empty(_scene.Canvas.Named("tip.panel").Where(c => c.Kind == CanvasCallKind.Sprite));
    }

    [Fact]
    public void Tooltip_OfAWornItemShowsOnTheWell()
    {
        _scene.Worn[2] = InventoryScene.Item(9, "helm.gilded", HeadMask);
        _scene.Refresh();

        _scene.Input.Pointer = InventoryScene.Centre(_scene.Well(2));
        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("Item 9"));
    }

    [Fact]
    public void HoverFrame_AllocatesNothingOnceWarm()
    {
        _scene.Items[5] = InventoryScene.Item(31, "ring.ice", rarity: ItemRarity.Rare, stack: 3);
        _scene.Worn[2] = InventoryScene.Item(9, "helm.gilded", HeadMask);
        _scene.Refresh();
        _scene.Input.Pointer = InventoryScene.Centre(_scene.Slot(5));
        NullCanvas canvas = new(_scene.Table);
        for (int n = 0; n < 3; n++)
        {
            InventoryWindow.Build(canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _scene.Actions);
            ItemOverlays.Build(canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _scene.Actions);
        }

        long bytes = Dream.Tests.Utilities.AllocationAssert.MeasureBytesPerIteration(() =>
        {
            InventoryWindow.Build(canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _scene.Actions);
            ItemOverlays.Build(canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _scene.Actions);
        });

        Assert.Equal(0L, bytes);
    }

    // ---- tier style -------------------------------------------------------------------------

    [Theory]
    [InlineData(ItemRarity.Common)]
    [InlineData(ItemRarity.Uncommon)]
    [InlineData(ItemRarity.Rare)]
    [InlineData((ItemRarity)99)]
    [InlineData((ItemRarity)255)]
    public void TierOf_AgreesWithRarityColorKey(ItemRarity rarity)
    {
        Assert.Equal((byte)CharacterPanelModel.ColorKey(rarity), (byte)ItemTierStyle.TierOf(rarity));
    }

    [Fact]
    public void TierStyle_ColoursAndPages()
    {
        Assert.Equal(new UiColor(0xF9, 0xF8, 0xFA, 255), ItemTierStyle.ColorOf(ItemTier.Common));
        Assert.Equal(new UiColor(0xF0, 0x8F, 0xB0, 255), ItemTierStyle.ColorOf(ItemTier.Uncommon));
        Assert.Equal(new UiColor(0x5F, 0xD6, 0xA0, 255), ItemTierStyle.ColorOf(ItemTier.Rare));
        Assert.Equal(UiPage.ItemsCommon, ItemTierStyle.PageOf(ItemTier.Common));
        Assert.Equal(UiPage.ItemsUncommon, ItemTierStyle.PageOf(ItemTier.Uncommon));
        Assert.Equal(UiPage.ItemsRare, ItemTierStyle.PageOf(ItemTier.Rare));
    }

    [Fact]
    public void CanEquipTo_ReadsTheSlotMask()
    {
        Assert.True(CharacterPanelModel.CanEquipTo(HeadMask, EquipmentSlot.Head));
        Assert.False(CharacterPanelModel.CanEquipTo(HeadMask, EquipmentSlot.Body));
        Assert.False(CharacterPanelModel.CanEquipTo(0, EquipmentSlot.MainHand));
        Assert.True(CharacterPanelModel.CanEquipTo(-1, EquipmentSlot.OffHand));
    }

    // Draws nothing, measures with a fixed width: enough to prove a steady frame allocates nothing.
    private sealed class NullCanvas(UiSpriteTable table) : IUiCanvas
    {
        public UiSpriteTable Table { get; } = table;

        public void Sprite(in UiSprite s, UiRect dst, UiColor tint) { }

        public void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint) { }

        public void NineSlice(in UiSprite s, UiRect dst, UiColor tint) { }

        public void Tile(in UiSprite s, UiRect dst, UiColor tint) { }

        public void Solid(UiRect dst, UiColor color) { }

        public void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim) { }

        public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color) => text.Length * 6f;

        public float MeasureText(ReadOnlySpan<char> text, UiTextStyle style) => text.Length * 6f;

        public void PushClip(UiRect r) { }

        public void PopClip() { }
    }
}
