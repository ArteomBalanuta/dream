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

    // A 2x3 blade on cell 0 (covering 0, 1, 8, 9, 16, 17), a 2x2 cap on cell 2 (2, 3, 10, 11) and a 1x2
    // torch on cell 4 (4, 12): the footprints are the item art's.
    public HudCapturePointerPlanTests()
    {
        _scene.Items[0] = InventoryScene.Item(11, "gear.iron-blade", MainHandMask, ItemRarity.Uncommon);
        _scene.Items[2] = InventoryScene.Item(12, "gear.leather-cap", HeadMask, ItemRarity.Rare);
        _scene.Items[4] = InventoryScene.Item(13, "tool.torch");
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

    private bool Build(HudCaptureStepSpec spec)
    {
        InventoryUiState ui = InventoryUiState.Current;
        UiRect? row = spec.Verb == HudCaptureVerb.Menu && spec.A < ui.MenuRowRects.Count ? ui.MenuRowRects[spec.A] : null;
        UiRect? confirm = spec.Verb == HudCaptureVerb.Confirm && ui.ConfirmOpen
            ? (spec.A == HudCaptureStepSpec.ConfirmDestroy ? ui.ConfirmDestroyRect : ui.ConfirmCancelRect)
            : null;
        UiLayout layout = _scene.Layout;
        return HudCapturePointerPlan.TryBuild(spec, in layout, _last, row, confirm, _frames, _scene.View);
    }

    private void Run(string token)
    {
        Assert.True(HudCaptureStepSpec.TryParse(token, out HudCaptureStepSpec spec), token);
        Assert.True(Build(spec), token);
        Play(_frames);
    }

    [Theory]
    [InlineData("hover:3", (int)HudCaptureVerb.Hover, 3, 0)]
    [InlineData("hover:63", (int)HudCaptureVerb.Hover, 63, 0)]
    [InlineData("hover:main", (int)HudCaptureVerb.Hover, 100, 0)]
    [InlineData("drag:0:5", (int)HudCaptureVerb.Drag, 0, 5)]
    [InlineData("drag:27:63", (int)HudCaptureVerb.Drag, 27, 63)]
    [InlineData("dragging:1:head", (int)HudCaptureVerb.Dragging, 1, 102)]
    [InlineData("rightclick:15", (int)HudCaptureVerb.RightClick, 15, 0)]
    [InlineData("rightclick:16", (int)HudCaptureVerb.RightClick, 16, 0)]
    [InlineData("menu:2", (int)HudCaptureVerb.Menu, 2, 0)]
    [InlineData("confirm:destroy", (int)HudCaptureVerb.Confirm, 0, 0)]
    [InlineData("confirm:cancel", (int)HudCaptureVerb.Confirm, 1, 0)]
    [InlineData("wait:30", (int)HudCaptureVerb.Wait, 30, 0)]
    [InlineData("close", (int)HudCaptureVerb.Close, 0, 0)]
    [InlineData("size:1100:700", (int)HudCaptureVerb.Size, 1100, 700)]
    [InlineData("esc", (int)HudCaptureVerb.Escape, 0, 0)]
    [InlineData("v", (int)HudCaptureVerb.Key, 0, 0)]
    [InlineData("fight:600", (int)HudCaptureVerb.Fight, 600, 0)]
    public void Grammar_ParsesEachStep(string token, int verb, int a, int b)
    {
        Assert.True(HudCaptureStepSpec.TryParse(token, out HudCaptureStepSpec spec));
        Assert.Equal(new HudCaptureStepSpec((HudCaptureVerb)verb, a, b), spec);
    }

    [Theory]
    [InlineData("main", 100)]
    [InlineData("off", 101)]
    [InlineData("head", 102)]
    [InlineData("body", 103)]
    [InlineData("hands", 104)]
    [InlineData("feet", 105)]
    [InlineData("legs", 106)]
    [InlineData("neck", 107)]
    [InlineData("ring1", 108)]
    [InlineData("ring2", 109)]
    [InlineData("back", 110)]
    [InlineData("pet", 111)]
    [InlineData("100", 100)]
    [InlineData("111", 111)]
    [InlineData("0", 0)]
    [InlineData("63", 63)]
    public void Slots_ParseAsAnchorsOrWellNames(string text, int expected)
    {
        Assert.True(HudCaptureStepSpec.TryParseSlot(text, out int slot));
        Assert.Equal(expected, slot);
    }

    [Fact]
    public void SlotNames_AreTheTwelveWellsInEquipmentOrder()
    {
        string[] names = ["main", "off", "head", "body", "hands", "feet", "legs", "neck", "ring1", "ring2", "back", "pet"];

        for (int n = 0; n < names.Length; n++)
        {
            Assert.True(HudCaptureStepSpec.TryParseSlot(names[n], out int slot));
            Assert.Equal(EquipmentSlots.ToSlotIndex((EquipmentSlot)n), slot);
        }
    }

    [Theory]
    [InlineData("hover")]
    [InlineData("hover:64")]
    [InlineData("hover:99")]
    [InlineData("hover:112")]
    [InlineData("hover:-1")]
    [InlineData("hover:200")]
    [InlineData("hover:ring")]
    [InlineData("hover:ring3")]
    [InlineData("hover:Hands")]
    [InlineData("drag:1")]
    [InlineData("drag:1:x")]
    [InlineData("drag:64:1")]
    [InlineData("drag:1:112")]
    [InlineData("menu:99")]
    [InlineData("confirm:maybe")]
    [InlineData("wait:0")]
    [InlineData("wait:999999")]
    [InlineData("play:1")]
    [InlineData("size:100:100")]
    [InlineData("size:1100")]
    [InlineData("fight")]
    [InlineData("fight:0")]
    [InlineData("fight:99999")]
    [InlineData("fight:x")]
    public void Grammar_DropsMalformedSteps(string token)
    {
        Assert.False(HudCaptureStepSpec.TryParse(token, out _));
    }

    [Fact]
    public void Script_KeepsPointerSteps_AndNamesTheirFiles()
    {
        HudCaptureScript script = HudCaptureScript.Parse("shots", "inventory,hover:3,Drag:0:5,bogus:1,release,drag:ring1:ring2")!;

        Assert.Equal(["inventory", "hover:3", "drag:0:5", "release", "drag:ring1:ring2"], script.Steps);
        Assert.Equal(Path.Combine("shots", "03-drag-0-5.png"), script.FilePath(2));
        Assert.Equal(Path.Combine("shots", "05-drag-ring1-ring2.png"), script.FilePath(4));
    }

    [Fact]
    public void Script_HoldsAFullRunOfSteps()
    {
        // The plan's real-GL script is long: hovers, drags, snaps for two sizes.
        string many = string.Join(',', Enumerable.Repeat("snap", 60));

        Assert.Equal(60, HudCaptureScript.Parse("out", many)!.Steps.Count);
        Assert.True(HudCaptureScript.MaxSteps >= 60);
    }

    [Fact]
    public void Drag_FirstMoveExceedsTheRouterThreshold_AndEndsOnTheTarget()
    {
        UiLayout layout = _scene.Layout;
        HudCaptureStepSpec spec = new(HudCaptureVerb.Drag, 0, 5);
        Assert.True(Build(spec));

        Vector2 from = InventoryScene.Centre(_scene.Slot(0));
        int firstMove = _frames.FindIndex(f => f.LeftDown && f.Position != from);
        Assert.True((_frames[firstMove].Position - from).Length > UiInputRouter.DragThresholdPixels);
        // The blade covers 2x3 cells, so the drop point is the middle of that footprint placed on cell 5.
        UiRect footprint = InventoryWindow.ItemRect(layout, layout.Inventory, 5, new ItemFootprint(2, 3));
        Assert.Equal(InventoryScene.Centre(footprint), _frames[^1].Position);
        Assert.False(_frames[^1].LeftDown);
        Assert.True(_frames.Take(HudCapturePointerPlan.HoverLeadFrames).All(f => !f.LeftDown && f.Position == from));
    }

    [Fact]
    public void Dragging_KeepsTheButtonDown()
    {
        Assert.True(Build(new(HudCaptureVerb.Dragging, 0, 5)));

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
    public void Hover_OnTheAnchorCellOfEachItem_SelectsThatItem()
    {
        foreach (int anchor in new[] { 0, 2, 4 })
        {
            Run($"hover:{anchor}");

            Assert.Equal(anchor, InventoryUiState.Current.HoverSource);
        }
    }

    [Fact]
    public void DragBagToBag_MovesTheItem()
    {
        Run("drag:0:5");

        InventoryHudAction action = Assert.Single(_scene.Actions.Picked);
        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 5), action);
    }

    [Theory]
    [InlineData(27)]
    [InlineData(46)]
    [InlineData(21)]
    public void DragBagToAFreeAnchor_LandsTheItemsAnchorExactlyThere(int anchor)
    {
        Run($"drag:0:{anchor}");

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, anchor), Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void DragBagToAnAnchorWhereItDoesNotFit_LandsOnTheNearestOneThatDoes()
    {
        // A 2x3 blade cannot anchor on column 7 or row 7: the hand's drop would be clamped onto the board.
        Run("drag:0:63");

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 46), Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void DragOntoAnOccupiedBagSlot_IsAMove_WhichTheServerSwaps()
    {
        Run("drag:0:2");

        Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 0, 2), Assert.Single(_scene.Actions.Picked));
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
        Assert.True(Build(new(HudCaptureVerb.Dragging, 4, 100)));
        InventoryFakeCanvas held = Play(_frames);

        Assert.Contains(held.Calls, c => c.Name == UiSpriteNames.CellOverlayInvalid);

        Run("release");
        Assert.Empty(_scene.Actions.Picked);
    }

    [Fact]
    public void DragWellToBag_UnequipsToTheNamedCell()
    {
        _scene.Items[0] = null;
        _scene.Worn[0] = InventoryScene.Item(11, "gear.iron-blade", MainHandMask, ItemRarity.Uncommon);
        _scene.Refresh();

        Run("drag:main:6");

        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.Unequip, EquipmentSlots.FirstSlotIndex, 6),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void DragWellToTheLastColumn_UnequipsToTheClampedCell()
    {
        _scene.Items[0] = null;
        _scene.Worn[0] = InventoryScene.Item(11, "gear.iron-blade", MainHandMask, ItemRarity.Uncommon);
        _scene.Refresh();

        Run("drag:main:7");

        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.Unequip, EquipmentSlots.FirstSlotIndex, 6),
            Assert.Single(_scene.Actions.Picked));
    }

    [Theory]
    [InlineData(EquipmentSlot.Hands, "hands")]
    [InlineData(EquipmentSlot.Feet, "feet")]
    [InlineData(EquipmentSlot.Legs, "legs")]
    [InlineData(EquipmentSlot.Neck, "neck")]
    [InlineData(EquipmentSlot.RingLeft, "ring1")]
    [InlineData(EquipmentSlot.RingRight, "ring2")]
    [InlineData(EquipmentSlot.Back, "back")]
    [InlineData(EquipmentSlot.Companion, "pet")]
    public void DragBagToTheNewWells_ByName_Equips(EquipmentSlot slot, string name)
    {
        _scene.Items[20] = InventoryScene.Item(31, "gem.amber", EquipmentSlots.MaskOf(slot));
        _scene.Refresh();

        Run($"drag:20:{name}");

        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.EquipTo, 20, EquipmentSlots.ToSlotIndex(slot)),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void DragRing1ToRing2_MovesWellToWell()
    {
        int rings = EquipmentSlots.MaskOf(EquipmentSlot.RingLeft) | EquipmentSlots.MaskOf(EquipmentSlot.RingRight);
        _scene.Worn[(int)EquipmentSlot.RingLeft] = InventoryScene.Item(31, "gear.iron-band", rings);
        _scene.Refresh();

        Run("drag:ring1:ring2");

        Assert.Equal(
            new InventoryHudAction(InventoryHudActionKind.EquipTo, 108, 109),
            Assert.Single(_scene.Actions.Picked));
    }

    [Fact]
    public void Dragging_HidesTheTooltip_AndShowsTheGhost()
    {
        Assert.True(Build(new(HudCaptureVerb.Dragging, 0, 5)));

        InventoryFakeCanvas held = Play(_frames);

        Assert.True(_router.Dragging);
        Assert.DoesNotContain(held.Calls, c => c.Kind == CanvasCallKind.Text && c.Text == "Item 11");
        // The item in place, dimmed, and the ghost on the pointer.
        Assert.Equal(2, held.Named("item.gear.iron-blade").Count());
        Assert.Equal(6, held.Named("cell.overlay.hover").Count());
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
        Run("rightclick:4");
        int destroyRow = InventoryUiState.Current.MenuEntries.FindIndex(e => e.RequiresConfirm);
        Assert.True(destroyRow >= 0);

        Run($"menu:{destroyRow}");
        Assert.True(InventoryUiState.Current.ConfirmOpen);
        Assert.Empty(_scene.Actions.Picked);

        Run("confirm:cancel");
        Assert.False(InventoryUiState.Current.ConfirmOpen);
        Assert.Empty(_scene.Actions.Picked);

        Run("rightclick:4");
        Run($"menu:{destroyRow}");
        Run("confirm:destroy");

        InventoryHudAction action = Assert.Single(_scene.Actions.Picked);
        Assert.Equal(InventoryHudActionKind.Destroy, action.Kind);
        Assert.Equal(4, action.SlotIndex);
        Assert.Equal(13, action.ItemInstanceId);
    }

    [Fact]
    public void Burst_SendsOneMovePerDrop_BetweenTwoOccupiedSlots()
    {
        // Two same-sized items, so each leg lands exactly on the other's anchor.
        _scene.Items[40] = InventoryScene.Item(41, "gem.amber");
        _scene.Items[41] = InventoryScene.Item(42, "gear.iron-band");
        _scene.Refresh();

        Run("burst:40:41:6");

        Assert.Equal(6, _scene.Actions.Picked.Count);
        for (int n = 0; n < 6; n++)
        {
            Assert.Equal(new InventoryHudAction(InventoryHudActionKind.Move, 40 + (n % 2), 40 + ((n + 1) % 2)), _scene.Actions.Picked[n]);
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

        for (int slot = 0; slot < 64; slot++)
        {
            Assert.True(HudCapturePointerPlan.TryCentre(in layout, slot, out Vector2 c));
            Assert.Equal(slot, InventoryWindow.HitTest(layout, layout.Inventory, c));
        }

        for (int well = 0; well < 12; well++)
        {
            Assert.True(HudCapturePointerPlan.TryCentre(in layout, EquipmentSlots.FirstSlotIndex + well, out Vector2 c));
            Assert.Equal(EquipmentSlots.FirstSlotIndex + well, InventoryWindow.HitTest(layout, layout.Inventory, c));
        }

        Assert.False(HudCapturePointerPlan.TryCentre(in layout, 64, out _));
        Assert.False(HudCapturePointerPlan.TryCentre(in layout, 99, out _));
        Assert.False(HudCapturePointerPlan.TryCentre(in layout, 112, out _));
        Assert.False(HudCapturePointerPlan.TryCentre(in layout, -1, out _));
    }

    [Theory]
    [InlineData(1280, 720, 0f)]
    [InlineData(1920, 1080, 0f)]
    [InlineData(1440, 1024, 3f)]
    public void DropPoints_LandAFootprintOnItsAnchor_OrOnTheNearestOneThatFits(int w, int h, float configured)
    {
        UiLayout layout = UiLayout.Create(w, h, configured);
        UiRect board = InventoryWindow.BagRect(layout, layout.Inventory);

        for (int cols = 1; cols <= 4; cols++)
        {
            for (int rows = 1; rows <= 4; rows++)
            {
                ItemFootprint footprint = new((byte)cols, (byte)rows);
                for (int anchor = 0; anchor < 64; anchor++)
                {
                    Assert.True(HudCapturePointerPlan.TryDropPoint(in layout, anchor, footprint, out Vector2 point));
                    Assert.True(board.Contains(point), $"{cols}x{rows} at {anchor}");
                    Assert.True(InventoryWindow.TryDropAnchor(layout, layout.Inventory, point, footprint, out int landed));
                    int expected = (Math.Min(anchor / 8, 8 - rows) * 8) + Math.Min(anchor % 8, 8 - cols);
                    Assert.Equal(expected, landed);
                }
            }
        }
    }

    [Fact]
    public void ClickProofVerbs_Parse()
    {
        Assert.True(HudCaptureStepSpec.TryParse("enemyclick:left", out HudCaptureStepSpec left));
        Assert.Equal(HudCaptureVerb.EnemyClick, left.Verb);
        Assert.Equal(0, left.A);
        Assert.True(HudCaptureStepSpec.TryParse("enemyclick:right", out HudCaptureStepSpec right));
        Assert.Equal(1, right.A);

        Assert.True(HudCaptureStepSpec.TryParse("groundclick:12:9", out HudCaptureStepSpec ground));
        Assert.Equal((HudCaptureVerb.GroundClick, 12, 9), (ground.Verb, ground.A, ground.B));

        Assert.True(HudCaptureStepSpec.TryParse("holdmove:8:8:11:5:150", out HudCaptureStepSpec hold));
        Assert.Equal(HudCaptureVerb.HoldMove, hold.Verb);
        Assert.Equal(8 | (8 << 16), hold.A);
        Assert.Equal(11 | (5 << 16), hold.B);
        Assert.Equal(150, hold.C);

        Assert.True(HudCaptureStepSpec.TryParse("keys:w+space:30", out HudCaptureStepSpec keys));
        Assert.Equal(HudCaptureVerb.Keys, keys.Verb);
        Assert.Equal((int)(HudCaptureKeys.W | HudCaptureKeys.Space), keys.A);
        Assert.Equal(30, keys.B);

        Assert.True(HudCaptureStepSpec.TryParse("keys:q+r:3", out HudCaptureStepSpec potions));
        Assert.Equal((int)(HudCaptureKeys.Q | HudCaptureKeys.R), potions.A);
        Assert.True(HudCaptureStepSpec.TryParse("keys:b:2", out HudCaptureStepSpec building));
        Assert.Equal((int)HudCaptureKeys.Building, building.A);
        Assert.True(HudCaptureStepSpec.TryParse("keys:g:2", out HudCaptureStepSpec combat));
        Assert.Equal((int)HudCaptureKeys.Combat, combat.A);
        Assert.False(HudCaptureStepSpec.TryParse("keys:e:2", out _));

        Assert.True(HudCaptureStepSpec.TryParse("status", out HudCaptureStepSpec status));
        Assert.Equal(HudCaptureVerb.Status, status.Verb);
        Assert.True(HudCaptureStepSpec.TryParse("waitidle:600", out HudCaptureStepSpec idle));
        Assert.Equal((HudCaptureVerb.WaitIdle, 600), (idle.Verb, idle.A));
    }

    [Theory]
    [InlineData("enemyclick")]
    [InlineData("enemyclick:middle")]
    [InlineData("groundclick:1")]
    [InlineData("holdmove:1:2:3:4")]
    [InlineData("holdmove:1:2:3:4:0")]
    [InlineData("holdmove:-1:2:3:4:10")]
    [InlineData("keys:z:10")]
    [InlineData("keys:w:0")]
    [InlineData("keys::10")]
    [InlineData("waitidle:0")]
    [InlineData("status:1")]
    public void ClickProofVerbs_RejectMalformedTokens(string token)
    {
        Assert.False(HudCaptureStepSpec.TryParse(token, out _));
    }

    [Fact]
    public void HoldAndMove_PressesThroughTheMoveAndReleasesAtTheEnd()
    {
        List<ScriptedPointerFrame> frames = new();

        HudCapturePointerPlan.HoldAndMove(frames, new Vector2(100f, 100f), new Vector2(200f, 100f), 10);

        Assert.Equal(HudCapturePointerPlan.HoverLeadFrames, frames.TakeWhile(f => !f.LeftDown).Count());
        Assert.All(frames.Skip(HudCapturePointerPlan.HoverLeadFrames).Take(10), f => Assert.True(f.LeftDown));
        Assert.Equal(new Vector2(200f, 100f), frames[HudCapturePointerPlan.HoverLeadFrames + 9].Position);
        Assert.False(frames[^1].LeftDown);
    }

    [Fact]
    public void HoldAndMove_RightHoldsTheRightButtonOnly()
    {
        List<ScriptedPointerFrame> frames = new();

        HudCapturePointerPlan.HoldAndMove(frames, new Vector2(100f, 100f), new Vector2(200f, 100f), 10, right: true);

        Assert.All(frames.Skip(HudCapturePointerPlan.HoverLeadFrames).Take(10), f => Assert.True(f.RightDown && !f.LeftDown));
        Assert.All(frames.Take(HudCapturePointerPlan.HoverLeadFrames), f => Assert.False(f.LeftDown || f.RightDown));
        Assert.False(frames[^1].LeftDown || frames[^1].RightDown);
    }

    [Fact]
    public void AimAndHoldLeftTokens_Parse()
    {
        Assert.True(HudCaptureStepSpec.TryParse("aim:900:420", out HudCaptureStepSpec aim));
        Assert.Equal((HudCaptureVerb.Aim, 900, 420), (aim.Verb, aim.A, aim.B));
        Assert.True(HudCaptureStepSpec.TryParse("holdleft:180", out HudCaptureStepSpec hold));
        Assert.Equal((HudCaptureVerb.HoldLeft, 180), (hold.Verb, hold.A));
        Assert.False(HudCaptureStepSpec.TryParse("aim:900", out _));
        Assert.False(HudCaptureStepSpec.TryParse("aim:-1:5", out _));
        Assert.False(HudCaptureStepSpec.TryParse("holdleft:0", out _));
        Assert.False(HudCaptureStepSpec.TryParse("holdleft", out _));
    }

    [Fact]
    public void Aim_RestsWithNoButtonAtThePixel()
    {
        List<ScriptedPointerFrame> frames = new();

        HudCapturePointerPlan.Aim(frames, new Vector2(900f, 420f));

        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.True(f.Position == new Vector2(900f, 420f) && !f.LeftDown && !f.RightDown));
    }

    [Fact]
    public void HoldLeft_PressesOnTheFirstFrameForTheCountThenReleasesInPlace()
    {
        List<ScriptedPointerFrame> frames = new();

        HudCapturePointerPlan.HoldLeft(frames, new Vector2(900f, 420f), 5);

        Assert.Equal(6, frames.Count);
        Assert.All(frames.Take(5), f => Assert.True(f.LeftDown && !f.RightDown));
        Assert.False(frames[^1].LeftDown);
        Assert.All(frames, f => Assert.Equal(new Vector2(900f, 420f), f.Position));
    }
}
