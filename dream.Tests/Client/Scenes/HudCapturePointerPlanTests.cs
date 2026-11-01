using Dream.Client.Scenes;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using Dream.Tests.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Scenes;

// The capture script's pointer steps: parsed, then played through the real UiInputRouter and the real
// panels, which must answer with the same inventory actions a hand on the mouse would.
public sealed class HudCapturePointerPlanTests : IDisposable
{
    private static readonly int MainHandMask = EquipmentSlots.MaskOf(EquipmentSlot.MainHand);
    private static readonly int HeadMask = EquipmentSlots.MaskOf(EquipmentSlot.Head);

    private readonly InventoryScene _scene = new();
    private readonly UiInputRouter _router = new();
    private readonly List<ScriptedPointerFrame> _frames = new();
    private Vector2 _last;

    public HudCapturePointerPlanTests()
    {
        _scene.Items[0] = InventoryScene.Item(11, "gear.iron-blade", MainHandMask, ItemRarity.Uncommon);
        _scene.Items[1] = InventoryScene.Item(12, "gear.leather-cap", HeadMask, ItemRarity.Rare);
        _scene.Items[2] = InventoryScene.Item(13, "tool.torch");
        _scene.Refresh();
        _last = HudCapturePointerPlan.Parked(_scene.Layout);
    }

    public void Dispose() => _scene.Dispose();

    private InventoryFakeCanvas Play(IEnumerable<ScriptedPointerFrame> frames)
    {
        InventoryFakeCanvas canvas = new(_scene.Table);
        UiLayout layout = _scene.Layout;
        foreach (ScriptedPointerFrame f in frames)
        {
            canvas = new InventoryFakeCanvas(_scene.Table);
            _router.BeginFrame(new UiPointer(f.Position, f.LeftDown, f.RightDown, true));
            ClassicHud.Build(canvas, _router, _scene.View, in layout, _scene.Table, _scene.Actions);
            _router.EndFrame();
            _last = f.Position;
        }

        return canvas;
    }

    private void Run(string token)
    {
        Assert.True(HudCaptureStepSpec.TryParse(token, out HudCaptureStepSpec spec), token);
        InventoryUiState ui = InventoryUiState.Current;
        UiRect? row = spec.Verb == HudCaptureVerb.Menu && spec.A < ui.MenuRowRects.Count ? ui.MenuRowRects[spec.A] : null;
        UiRect? confirm = spec.Verb == HudCaptureVerb.Confirm && ui.ConfirmOpen
            ? (spec.A == HudCaptureStepSpec.ConfirmDestroy ? ui.ConfirmDestroyRect : ui.ConfirmCancelRect)
            : null;
        UiLayout layout = _scene.Layout;
        Assert.True(HudCapturePointerPlan.TryBuild(spec, in layout, _last, row, confirm, _frames), token);
        Play(_frames);
    }

    [Theory]
    [InlineData("hover:3", (int)HudCaptureVerb.Hover, 3, 0)]
    [InlineData("hover:main", (int)HudCaptureVerb.Hover, 100, 0)]
    [InlineData("drag:0:5", (int)HudCaptureVerb.Drag, 0, 5)]
    [InlineData("dragging:1:head", (int)HudCaptureVerb.Dragging, 1, 102)]
    [InlineData("rightclick:15", (int)HudCaptureVerb.RightClick, 15, 0)]
    [InlineData("menu:2", (int)HudCaptureVerb.Menu, 2, 0)]
    [InlineData("confirm:destroy", (int)HudCaptureVerb.Confirm, 0, 0)]
    [InlineData("confirm:cancel", (int)HudCaptureVerb.Confirm, 1, 0)]
    [InlineData("wait:30", (int)HudCaptureVerb.Wait, 30, 0)]
    [InlineData("close", (int)HudCaptureVerb.Close, 0, 0)]
    [InlineData("size:1100:700", (int)HudCaptureVerb.Size, 1100, 700)]
    [InlineData("esc", (int)HudCaptureVerb.Escape, 0, 0)]
    [InlineData("v", (int)HudCaptureVerb.Key, 0, 0)]
    public void Grammar_ParsesEachStep(string token, int verb, int a, int b)
    {
        Assert.True(HudCaptureStepSpec.TryParse(token, out HudCaptureStepSpec spec));
        Assert.Equal(new HudCaptureStepSpec((HudCaptureVerb)verb, a, b), spec);
    }

    [Theory]
    [InlineData("hover")]
    [InlineData("hover:16")]
    [InlineData("hover:99")]
    [InlineData("hover:-1")]
    [InlineData("drag:1")]
    [InlineData("drag:1:x")]
    [InlineData("menu:99")]
    [InlineData("confirm:maybe")]
    [InlineData("wait:0")]
    [InlineData("wait:999999")]
    [InlineData("play:1")]
    [InlineData("size:100:100")]
    [InlineData("size:1100")]
    public void Grammar_DropsMalformedSteps(string token)
    {
        Assert.False(HudCaptureStepSpec.TryParse(token, out _));
    }

    [Fact]
    public void Script_KeepsPointerSteps_AndNamesTheirFiles()
    {
        HudCaptureScript script = HudCaptureScript.Parse("shots", "inventory,hover:3,Drag:0:5,bogus:1,release")!;

        Assert.Equal(["inventory", "hover:3", "drag:0:5", "release"], script.Steps);
        Assert.Equal(Path.Combine("shots", "03-drag-0-5.png"), script.FilePath(2));
    }

    [Fact]
    public void Drag_FirstMoveExceedsTheRouterThreshold_AndEndsOnTheTarget()
    {
        UiLayout layout = _scene.Layout;
        HudCaptureStepSpec spec = new(HudCaptureVerb.Drag, 0, 5);
        Assert.True(HudCapturePointerPlan.TryBuild(spec, in layout, _last, null, null, _frames));

        Vector2 from = InventoryScene.Centre(_scene.Slot(0));
        Vector2 to = InventoryScene.Centre(_scene.Slot(5));
        int firstMove = _frames.FindIndex(f => f.LeftDown && f.Position != from);
        Assert.True((_frames[firstMove].Position - from).Length > UiInputRouter.DragThresholdPixels);
        Assert.Equal(to, _frames[^1].Position);
        Assert.False(_frames[^1].LeftDown);
        Assert.True(_frames.Take(HudCapturePointerPlan.HoverLeadFrames).All(f => !f.LeftDown && f.Position == from));
    }

    [Fact]
    public void Dragging_KeepsTheButtonDown()
    {
        UiLayout layout = _scene.Layout;
        Assert.True(HudCapturePointerPlan.TryBuild(new(HudCaptureVerb.Dragging, 0, 5), in layout, _last, null, null, _frames));

        Assert.True(_frames[^1].LeftDown);
    }

    [Fact]
    public void Hover_ShowsTheTooltip_WithRolledStats()
    {
        Run("hover:0");

        Assert.True(InventoryUiState.Current.HoverActive);
        Assert.Equal(0, InventoryUiState.Current.HoverSource);
    }

    [Fact]
    public void DragBagToBag_MovesTheItem()
    {
        Run("drag:0:5");

        InventoryHudAction action = Assert.Single(_scene.Actions.Picked);
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 5), action);
    }

    [Fact]
    public void DragOntoAnOccupiedBagSlot_IsAMove_WhichTheServerSwaps()
    {
        Run("drag:0:1");

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 1), Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void DragBagToACompatibleWell_Equips()
    {
        Run("drag:0:main");

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.EquipTo, 0, EquipmentSlots.FirstSlotIndex), Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void DragBagToAnIncompatibleWell_ShowsInvalid_AndSendsNothing()
    {
        UiLayout layout = _scene.Layout;
        Assert.True(HudCapturePointerPlan.TryBuild(new(HudCaptureVerb.Dragging, 2, 100), in layout, _last, null, null, _frames));
        InventoryFakeCanvas held = Play(_frames);

        Assert.Contains(held.Calls, c => c.Name == UiSpriteNames.CellOverlayInvalid);

        Run("release");
        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void DragWellToBag_Unequips()
    {
        _scene.Worn[0] = _scene.Items[0];
        _scene.Equipped[0] = true;
        _scene.Refresh();

        Run("drag:main:7");

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Unequip, EquipmentSlots.FirstSlotIndex), Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Dragging_HidesTheTooltip_AndShowsTheGhost()
    {
        UiLayout layout = _scene.Layout;
        Assert.True(HudCapturePointerPlan.TryBuild(new(HudCaptureVerb.Dragging, 0, 5), in layout, _last, null, null, _frames));

        InventoryFakeCanvas held = Play(_frames);

        Assert.True(_router.Dragging);
        Assert.DoesNotContain(held.Calls, c => c.Kind == CanvasCallKind.Text && c.Text == "Item 11");
    }

    [Fact]
    public void RightClick_OpensTheMenu_AndAPickedRowSendsOneIntent()
    {
        Run("rightclick:0");
        Assert.True(InventoryUiState.Current.MenuOpen);
        Assert.Empty(_scene.Actions.Picked);

        Run("menu:0");

        InventoryHudAction action = Assert.Single(_scene.Actions.Picked);
        Assert.Equal(InventoryHudActionKind.EquipTo, action.Kind);
        Assert.Equal(0, action.SlotIndex);
    }

    [Fact]
    public void Destroy_NeedsTheConfirm_AndCancelSendsNothing()
    {
        Run("rightclick:2");
        int destroyRow = InventoryUiState.Current.MenuEntries.FindIndex(e => e.RequiresConfirm);
        Assert.True(destroyRow >= 0);

        Run($"menu:{destroyRow}");
        Assert.True(InventoryUiState.Current.ConfirmOpen);
        Assert.Empty(_scene.Actions.Picked);

        Run("confirm:cancel");
        Assert.False(InventoryUiState.Current.ConfirmOpen);
        Assert.Empty(_scene.Actions.Picked);

        Run("rightclick:2");
        Run($"menu:{destroyRow}");
        Run("confirm:destroy");

        InventoryHudAction action = Assert.Single(_scene.Actions.Picked);
        Assert.Equal(InventoryHudActionKind.Destroy, action.Kind);
        Assert.Equal(13, action.ItemInstanceId);
    }

    [Fact]
    public void Burst_SendsOneMovePerDrop_BetweenTwoOccupiedSlots()
    {
        Run("burst:0:1:6");

        Assert.Equal(6, _scene.Actions.Picked.Count);
        for (int n = 0; n < 6; n++)
        {
            Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, n % 2, (n + 1) % 2), _scene.Actions.Picked[n]);
        }
    }

    [Fact]
    public void Close_ClicksTheInventoryCloseButton()
    {
        Run("close");

        Assert.Equal(1, _scene.Actions.Closes);
    }

    [Theory]
    [InlineData(1280, 720, 0f)]
    [InlineData(1920, 1080, 0f)]
    [InlineData(1440, 1024, 3f)]
    [InlineData(800, 600, 0f)]
    public void SlotCentres_AreInsideTheirSlots_AtAnySizeAndScale(int w, int h, float configured)
    {
        UiLayout layout = UiLayout.Create(w, h, configured);

        for (int slot = 0; slot < 16; slot++)
        {
            Assert.True(HudCapturePointerPlan.TryCentre(in layout, slot, out Vector2 c));
            Assert.Equal(slot, InventoryWindow.HitTest(layout, layout.Inventory, c));
        }

        for (int well = 0; well < 4; well++)
        {
            Assert.True(HudCapturePointerPlan.TryCentre(in layout, EquipmentSlots.FirstSlotIndex + well, out Vector2 c));
            Assert.Equal(EquipmentSlots.FirstSlotIndex + well, InventoryWindow.HitTest(layout, layout.Inventory, c));
        }
    }
}
