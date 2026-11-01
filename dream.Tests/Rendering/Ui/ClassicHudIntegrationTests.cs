using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using Dream.Tests.Rendering.Ui.Panels;
using Dream.Tests.Support;

namespace Dream.Tests.Rendering.Ui;

// The real router under the real panels, frame by frame, as ClassicHudSurface drives them. The panel
// tests use a scripted fake; these pin what only appears when the two meet.
public sealed class ClassicHudIntegrationTests : IDisposable
{
    private readonly InventoryScene _scene = new();
    private readonly UiInputRouter _router = new();

    public ClassicHudIntegrationTests()
    {
        _scene.Items[0] = InventoryScene.Item(11, "tool.torch");
        _scene.Items[1] = InventoryScene.Item(12, "tool.torch", rarity: ItemRarity.Rare);
        _scene.Refresh();
    }

    public void Dispose() => _scene.Dispose();

    private InventoryFakeCanvas Step(Vector2 position, bool left = false, bool right = false)
    {
        InventoryFakeCanvas canvas = new(_scene.Table);
        UiLayout layout = _scene.Layout;
        _router.BeginFrame(new UiPointer(position, left, right, true));
        ClassicHud.Build(canvas, _router, _scene.View, in layout, _scene.Table, _scene.Actions);
        _router.EndFrame();
        return canvas;
    }

    private void Click(Vector2 at)
    {
        Step(at);
        Step(at, left: true);
        Step(at);
    }

    private InventoryFakeCanvas RightClick(Vector2 at)
    {
        Step(at);
        Step(at, right: true);
        return Step(at);
    }

    private static Vector2 Centre(UiRect r) => InventoryScene.Centre(r);

    private static Vector2 At(CanvasCall text) => new(text.TextPos.X + 2f, text.TextPos.Y + 2f);

    // The whole path from a fresh snapshot to the drag: the builder decides what counts as a change,
    // the panels keep or drop the drag on it.
    [Fact]
    public void FreshSnapshotWithTheSameContents_KeepsTheDrag_ButAChangedOneCancelsIt()
    {
        HudView view = new() { InventoryOpen = true };
        HudViewBuilder builder = new(_scene.Table, view);
        HudStatusSnapshot status = new(10, 10, default, 1, 2, 3, 1f, 4, 1, 0, 10);
        builder.Refresh(status, _scene.View.Inventory);
        UiLayout layout = _scene.Layout;
        InventoryFakeCanvas Frame(Vector2 at, bool left)
        {
            InventoryFakeCanvas canvas = new(_scene.Table);
            _router.BeginFrame(new UiPointer(at, left, false, true));
            ClassicHud.Build(canvas, _router, view, in layout, _scene.Table, _scene.Actions);
            _router.EndFrame();
            return canvas;
        }

        Vector2 from = Centre(_scene.Slot(0));
        Vector2 to = Centre(_scene.Slot(5));
        Frame(from, false);
        Frame(from, true);
        Frame(from + new Vector2(12f, 0f), true);
        Assert.Equal(0, _router.ActiveDragPayload);

        // A new snapshot object with the same contents, and a health tick with it: still dragging.
        _scene.Refresh();
        builder.Refresh(status with { CurrentHealth = 9 }, _scene.View.Inventory);
        Assert.False(builder.InventoryChanged);
        Frame(to, true);
        Assert.Equal(0, _router.ActiveDragPayload);
        Frame(to, false);
        Frame(to, false);
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 5), Assert.Single(_scene.Actions.Picked));

        // Again, but the bag changes under the drag: it is cancelled and the drop sends nothing.
        _scene.Actions.Picked.Clear();
        Frame(from, false);
        Frame(from, true);
        Frame(from + new Vector2(12f, 0f), true);
        Assert.Equal(0, _router.ActiveDragPayload);
        _scene.Items[2] = InventoryScene.Item(13, "tool.torch");
        _scene.Refresh();
        builder.Refresh(status, _scene.View.Inventory);
        Assert.True(builder.InventoryChanged);
        _router.CancelDrag();
        Frame(to, true);
        Frame(to, false);
        Frame(to, false);
        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Drag_BagToBag_EmitsOneMove()
    {
        Vector2 from = Centre(_scene.Slot(0));
        Vector2 to = Centre(_scene.Slot(5));

        Step(from);
        Step(from, left: true);
        Assert.Equal(-1, _router.ActiveDragPayload);
        Step(from + new Vector2(10f, 0f), left: true);
        Assert.Equal(0, _router.ActiveDragPayload);
        Step(to, left: true);
        Assert.Equal(0, _router.ActiveDragPayload);
        Step(to);
        Step(to);

        InventoryHudAction move = Assert.Single(_scene.Actions.Picked);
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 5), move);
        Assert.Equal(-1, _router.ActiveDragPayload);
        Assert.False(InventoryUiState.Current.DragActive);
    }

    [Fact]
    public void RightClick_OpensTheMenu_AndAnEntryInsideTheModalAnswers()
    {
        InventoryFakeCanvas open = RightClick(Centre(_scene.Slot(0)));
        Assert.True(InventoryUiState.Current.MenuOpen);
        Assert.True(_router.ModalActive);

        CanvasCall destroy = open.Texts.First(t => t.Text == "Destroy...");
        Click(At(destroy));

        Assert.False(InventoryUiState.Current.MenuOpen);
        Assert.True(InventoryUiState.Current.ConfirmOpen);
        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Confirm_ButtonsAnswerInsideTheModal_AndDestroyEmitsOnce()
    {
        InventoryFakeCanvas open = RightClick(Centre(_scene.Slot(0)));
        Vector2 entry = At(open.Texts.First(t => t.Text == "Destroy..."));
        Step(entry);
        Step(entry, left: true);
        InventoryFakeCanvas confirm = Step(entry);
        CanvasCall button = confirm.Texts.Last(t => t.Text == "Destroy");

        Click(At(button));

        InventoryHudAction action = Assert.Single(_scene.Actions.Picked);
        Assert.Equal(11, action.ItemInstanceId);
        Assert.False(InventoryUiState.Current.ConfirmOpen);
    }

    [Fact]
    public void Modal_BlocksWidgetsBuiltBeforeIt()
    {
        RightClick(Centre(_scene.Slot(0)));

        UiRect satchel = _scene.Layout.FromUnits(_scene.Layout.CentreGroup, 408f - UiLayout.LeftGroupUnits, 18f, 28f, 28f);
        Click(Centre(satchel));

        Assert.Equal(0, _scene.Actions.Closes);
    }

    [Fact]
    public void OpeningClick_DoesNotCloseTheMenuOnItsOwnBackdrop()
    {
        RightClick(Centre(_scene.Slot(0)));
        Step(Centre(_scene.Slot(0)));

        Assert.True(InventoryUiState.Current.MenuOpen);
    }

    [Fact]
    public void BackdropClick_ClosesTheMenu_AndWidgetsComeBack()
    {
        RightClick(Centre(_scene.Slot(0)));
        Click(new Vector2(20f, 20f));
        Assert.False(InventoryUiState.Current.MenuOpen);

        Step(new Vector2(20f, 20f));
        Vector2 slot = Centre(_scene.Slot(0));
        Step(slot);
        Step(slot);
        Assert.False(_router.ModalActive);
        Assert.True(InventoryUiState.Current.HoverActive);
    }

    [Fact]
    public void CancelledDrag_DoesNotRestartWhileTheButtonIsHeld()
    {
        Vector2 from = Centre(_scene.Slot(0));
        Step(from);
        Step(from, left: true);
        Step(from + new Vector2(10f, 0f), left: true);
        Assert.Equal(0, _router.ActiveDragPayload);

        _router.CancelDrag();
        Step(from + new Vector2(20f, 0f), left: true);
        Step(from + new Vector2(30f, 0f), left: true);

        Assert.Equal(-1, _router.ActiveDragPayload);
        Step(from);
        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void Hover_ShowsTheTooltipAfterOneFrame()
    {
        Vector2 slot = Centre(_scene.Slot(0));
        Step(slot);
        InventoryFakeCanvas canvas = Step(slot);

        Assert.True(canvas.HasText("Item 11"));
    }

    [Fact]
    public void StatusChange_KeepsADrag_ButAnInventoryChangeReportsIt()
    {
        HudViewBuilder builder = new(_scene.Table, new HudView());
        PlayerInventorySnapshot? inventory = _scene.View.Inventory;
        HudStatusSnapshot before = new(10, 10, default, 1, 2, 3, 1f, 4, 1, 0, 10);
        HudStatusSnapshot after = new(9, 10, default, 1, 2, 3, 1f, 4, 1, 0, 10);
        builder.Refresh(before, inventory);

        Assert.True(builder.Refresh(after, inventory));
        Assert.False(builder.InventoryChanged);

        // A new snapshot object that reads the same is not a change, and keeps the old object so
        // the panels' own reference checks do not see one either.
        PlayerInventorySnapshot? old = builder.View.Inventory;
        _scene.Refresh();
        builder.Refresh(after, _scene.View.Inventory);
        Assert.False(builder.InventoryChanged);
        Assert.Same(old!.Value.Slots, builder.View.Inventory!.Value.Slots);

        _scene.Items[2] = InventoryScene.Item(13, "tool.torch");
        _scene.Refresh();
        builder.Refresh(after, _scene.View.Inventory);
        Assert.True(builder.InventoryChanged);
    }

    [Fact]
    public void TierIcon_UsesItsOwnPage_WithTheCommonRects()
    {
        UiBatch batch = new(BlockGlyphs.Create());
        batch.SetSpriteTable(_scene.Table);
        UiLayout layout = _scene.Layout;
        batch.BeginFrame(in layout);
        UiSprite icon = _scene.Table.Get("item.tool.torch");

        batch.Sprite(ItemTierStyle.IconFor(icon, ItemTier.Common), new UiRect(0, 0, 40, 40), UiColor.White);
        batch.Sprite(ItemTierStyle.IconFor(icon, ItemTier.Uncommon), new UiRect(50, 0, 40, 40), UiColor.White);
        batch.Sprite(ItemTierStyle.IconFor(icon, ItemTier.Rare), new UiRect(100, 0, 40, 40), UiColor.White);

        Assert.Equal(3, batch.CommandCount);
        Assert.Equal(UiPage.ItemsCommon, batch.Commands[0].Page);
        Assert.Equal(UiPage.ItemsUncommon, batch.Commands[1].Page);
        Assert.Equal(UiPage.ItemsRare, batch.Commands[2].Page);
        Assert.Equal(batch.Vertices[0].U, batch.Vertices[8].U);
        Assert.Equal(batch.Vertices[0].V, batch.Vertices[8].V);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void RealCell_TilesTwoByTwoInABagSlot_AtTheLayoutTexelScale(int width, int height)
    {
        UiLayout layout = UiLayout.Create(width, height, 0f);
        UiBatch batch = new(BlockGlyphs.Create());
        batch.SetSpriteTable(_scene.Table);
        batch.BeginFrame(in layout);
        UiRect slot = InventoryWindow.SlotRect(layout, layout.Inventory, 0);

        batch.Tile(_scene.Table.Get(UiSpriteNames.CellOccupied), slot, UiColor.White);

        Assert.Equal(4, batch.QuadCount);
        Assert.Equal(layout.Scale / 4f, batch.UnitScale);
    }

    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(2.25f, 1f)]
    [InlineData(3.75f, 1f)]
    [InlineData(4f, 2f)]
    public void TextScale_FollowsTheLayoutScaleInWholeSteps(float scale, float expected)
    {
        UiLayout layout = UiLayout.Create(2560, 1920, scale);

        Assert.Equal(expected, layout.TextScale);
    }

    [Fact]
    public void CombatTextProjection_UsesTheAnchorAlone()
    {
        string source = File.ReadAllText(Path.Combine(Dream.Tests.Integration.RepoRoot.Find(), "dream", "Rendering", "Core", "Render.Hud.cs"));
        int start = source.IndexOf("private void ProjectClassicCombatText", StringComparison.Ordinal);
        int end = source.IndexOf("private void ProjectClassicLabels", StringComparison.Ordinal);
        string body = source[start..end];

        Assert.Contains("TryProjectWorldToScreen(camera, state.WorldAnchor,", body);
        Assert.Contains("ScreenAnchor =", body);
        Assert.Contains("UsesScreenAnchor(state.Kind, viewMode, projected)", body);
        Assert.DoesNotContain("HorizontalOffset", body);
        Assert.DoesNotContain("VerticalOffset", body);
        Assert.DoesNotContain("DepthOffset", body);
    }
}
