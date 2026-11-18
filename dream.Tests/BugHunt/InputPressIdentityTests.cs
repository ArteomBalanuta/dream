using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Tests.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-INPUT-1 / BH-INPUT-2 / BH-INPUT-3: a control that acts on RELEASE must register an id that binds
// the identity it was drawn from. UiInputRouter only remembers the integer id across the press, so a
// row or label whose id is its POSITION in a live list can be redrawn under the pointer with another
// payload between the press and the release — and the release then acts on the new occupant.
//
// These drive the REAL UiInputRouter (the panel tests' fake input ignores ids entirely, so it cannot
// see this class of defect at all) over three headless frames, with no GL context:
//   frame 1  the pointer rests on the row      -> the row becomes hot for the next frame
//   frame 2  the button goes down              -> the router stores the press id
//   frame 3  the button comes up               -> Button() fires if the id still matches
// The mid-press change is applied between frames 2 and 3, which is exactly the real timing (12-20
// frames at 144 Hz for an 80-150 ms click).
public sealed class InputPressIdentityTests : IDisposable
{
    private readonly InventoryScene _scene = new();
    private readonly UiInputRouter _input = new();

    public void Dispose() => _scene.Dispose();

    private UiPointer At(Vector2 p, bool left) => new(p, left, false, true);

    private static Vector2 Centre(UiRect r) => new(r.X + (r.W * 0.5f), r.Y + (r.H * 0.5f));

    // One frame: BeginFrame, the panel builds, EndFrame.
    private void Frame(Vector2 at, bool left, Action build)
    {
        _input.BeginFrame(At(at, left));
        build();
        _input.EndFrame();
    }

    // Hover, press, (change), release. betweenPressAndRelease runs while the button is down.
    private void PressAndRelease(Vector2 at, Action build, Action? betweenPressAndRelease = null)
    {
        Frame(at, left: false, build);
        Frame(at, left: true, build);
        betweenPressAndRelease?.Invoke();
        Frame(at, left: false, build);
    }

    // ---- the helper itself ----

    [Fact]
    public void IdentityIds_AreInTheirOwnBand_AndDifferPerIdentity()
    {
        int a = HudWidgetId.Of(0x700, 4242L);
        int b = HudWidgetId.Of(0x700, 4243L);
        int sameIdentityOtherControl = HudWidgetId.Of(0x660, 4242L);

        Assert.Equal(a, HudWidgetId.Of(0x700, 4242L));
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, sameIdentityOtherControl);
        foreach (int id in new[] { a, b, sameIdentityOtherControl })
        {
            Assert.InRange(id, HudWidgetId.IdentityBand, HudWidgetId.IdentityBand + HudWidgetId.IdentityMask);
            // Clear of the ordinary HUD ids and of NpcDialogueWindow's own identity band.
            Assert.True(id > NpcDialogueWindow.IdActionBand + NpcDialogueWindow.IdActionBandMask);
        }
    }

    // ---- BH-INPUT-2: the trade bag row ----

    private readonly TradeHudState _trade = new();
    private readonly HudIntentQueue _intents = new();

    private UiRect Units(float x, float y, float w, float h) =>
        _scene.Layout.FromUnits(_scene.Layout.Character, x, y, w, h);

    private UiRect BagRow(int n) => Units(19f, 206f + (n * 13f), 152f, 12f);

    private void BuildTrade() =>
        TradeWindow.Build(_scene.Canvas, _input, _scene.View, _scene.Layout, _scene.Table, _scene.Actions, _trade, _intents);

    private void OpenTrade() =>
        _trade.Open(TradeHudStage.Open, "Bob the Bold", [], 0, false, false, [], 0, false, false);

    private void AddBagItems(int count)
    {
        for (int n = 0; n < count; n++)
        {
            _scene.Items[n] = InventoryScene.Item(100 + n, "gem.amber");
        }

        _scene.Refresh();
    }

    // The control: nothing changes mid-press, so the click still offers the item it was drawn from.
    [Fact]
    public void TradeBagRow_AStableBag_StillOffersTheItemTheRowWasDrawnFrom()
    {
        AddBagItems(4);
        OpenTrade();

        PressAndRelease(Centre(BagRow(2)), BuildTrade);

        Assert.True(_intents.TryDequeue(out IPlayerActionPayload payload));
        Assert.Equal([102L], Assert.IsType<TradeOfferIntent>(payload).ItemInstanceIds);
    }

    // BH-INPUT-2: a potion at a lower anchor leaves the bag while the button is down, so every later
    // row shifts up one and row 2 comes to hold item 103. The offer must never be item 103 — the
    // player pressed item 102. With the item in the id the press still matches the row item 102 moved
    // to, so the click lands on the item it was drawn from; with a position id it offered 103.
    [Fact]
    public void TradeBagRow_AnItemLeavingTheBagMidPress_NeverOffersTheItemThatMovedIntoTheRow()
    {
        AddBagItems(4);
        OpenTrade();

        PressAndRelease(
            Centre(BagRow(2)),
            BuildTrade,
            () =>
            {
                // Anchor 0 empties: rows 1..3 become rows 0..2, so row 2 now holds item 103.
                _scene.Items[0] = null;
                _scene.Refresh();
            });

        _intents.TryDequeue(out IPlayerActionPayload? payload);
        IReadOnlyList<long> offered = payload is TradeOfferIntent intent ? intent.ItemInstanceIds : [];
        Assert.DoesNotContain(103L, offered);
        Assert.Equal([102L], offered);
    }

    // The other half: the pressed item itself leaves the bag mid-press. No row carries its id any
    // more, so the click is dropped — the established safe failure mode — and nothing is offered.
    [Fact]
    public void TradeBagRow_ThePressedItemLeavingTheBagMidPress_OffersNothingAtAll()
    {
        AddBagItems(4);
        OpenTrade();

        PressAndRelease(
            Centre(BagRow(2)),
            BuildTrade,
            () =>
            {
                _scene.Items[2] = null;
                _scene.Refresh();
            });

        Assert.False(_intents.TryDequeue(out IPlayerActionPayload offered), $"the click was redirected: {offered}");
    }

    // ---- BH-INPUT-3: the vendor sell row ----

    private readonly VendorHudState _vendor = new();

    private UiRect SellRow(int n) => Units(19f, 182f + (n * 13f), 152f, 12f);

    private void BuildVendor() =>
        VendorWindow.Build(_scene.Canvas, _input, _scene.View, _scene.Layout, _scene.Table, _scene.Actions);

    private void OpenVendor()
    {
        _vendor.Open(900, "Old Mara", []);
        _scene.View.Vendor = _vendor;
    }

    // The control: a stable bag still selects the row's own item, and Sell still carries it.
    [Fact]
    public void VendorSellRow_AStableBag_StillSelectsTheItemTheRowWasDrawnFrom()
    {
        OpenVendor();
        AddBagItems(4);

        PressAndRelease(Centre(SellRow(2)), BuildVendor);

        Assert.Equal(2, _vendor.SelectedAnchor);
        Assert.Equal(102L, _vendor.SelectedItemId);
    }

    // BH-INPUT-3: the bag changes mid-press and row 2 comes to hold item 103. The selection must
    // never land on 103 — a player who presses Sell by reflex would sell it, and the server would
    // accept it, because the Sell step carries the selection's own instance id.
    [Fact]
    public void VendorSellRow_AnItemLeavingTheBagMidPress_NeverSelectsTheItemThatMovedIntoTheRow()
    {
        OpenVendor();
        AddBagItems(4);

        PressAndRelease(
            Centre(SellRow(2)),
            BuildVendor,
            () =>
            {
                _scene.Items[0] = null;
                _scene.Refresh();
            });

        Assert.NotEqual(103L, _vendor.SelectedItemId);
        Assert.Equal(102L, _vendor.SelectedItemId);
        Assert.Equal(2, _vendor.SelectedAnchor);
    }

    // The other half: the pressed item itself leaves the bag mid-press, so nothing carries its id and
    // the click is dropped. Nothing is selected, so Sell has nothing to send.
    [Fact]
    public void VendorSellRow_ThePressedItemLeavingTheBagMidPress_SelectsNothingAtAll()
    {
        OpenVendor();
        AddBagItems(4);

        PressAndRelease(
            Centre(SellRow(2)),
            BuildVendor,
            () =>
            {
                _scene.Items[2] = null;
                _scene.Refresh();
            });

        Assert.Equal(-1, _vendor.SelectedAnchor);
        Assert.Equal(0L, _vendor.SelectedItemId);
    }

    // ---- BH-INPUT-1: the world label ----

    private sealed class LabelActions : IHudActions
    {
        public List<long> PickedUp { get; } = [];

        public List<int> Talked { get; } = [];

        public void Inventory(InventoryHudAction action)
        {
        }

        public void CloseInventory()
        {
        }

        public void PickUpGround(long itemInstanceId) => PickedUp.Add(itemInstanceId);

        public void InteractNpc(int npcActorId) => Talked.Add(npcActorId);
    }

    private readonly LabelActions _labelActions = new();
    private readonly StatusBarRecordingCanvas _labelCanvas = new(StatusBarTestData.Table);
    private readonly HudView _labelView = StatusBarTestData.View();
    private static readonly UiLayout LabelLayout = UiLayout.Create(1920, 1080, 0f);

    private void BuildLabels() =>
        WorldLabelsPanel.Build(_labelCanvas, _input, _labelView, LabelLayout, _labelActions);

    private static HudWorldLabel Ground(float x, float y, string name, long id) =>
        new(new Vector2(x, y), name, string.Empty, new HudLabelClick(HudLabelClickKind.PickUpGround, id));

    private static HudWorldLabel Plain(float x, float y, string name) =>
        new(new Vector2(x, y), name, string.Empty);

    // The control: one clickable label, nothing changes, the click picks it up.
    [Fact]
    public void WorldLabel_AnOrdinaryClick_StillPicksUpTheLabelItWasDrawnOn()
    {
        _labelView.Labels = [Ground(800f, 400f, "Rusty Sword", 77L)];

        PressAndRelease(new Vector2(800f, 404f), BuildLabels);

        Assert.Equal([77L], _labelActions.PickedUp);
    }

    // BH-INPUT-1, the drop half: an unrelated actor's label appearing mid-press renumbered every
    // clickable label, so the click was silently lost. Bound to the target, the click survives.
    [Fact]
    public void WorldLabel_AnUnrelatedLabelAppearingMidPress_NoLongerLosesTheClick()
    {
        _labelView.Labels = [Ground(800f, 400f, "Rusty Sword", 77L)];

        PressAndRelease(
            new Vector2(800f, 404f),
            BuildLabels,
            // An enemy walks into view: it is appended BEFORE the economy labels, so the sword's
            // place in the list moves.
            () => _labelView.Labels = [Plain(1400f, 300f, "Wolf"), Ground(800f, 400f, "Rusty Sword", 77L)]);

        Assert.Equal([77L], _labelActions.PickedUp);
    }

    // The control for the stacked case: two ground items from one kill project to the same point, the
    // second is pushed below the first by the de-overlap pass. Nothing changes, so a click on the top
    // one picks up the top one.
    [Fact]
    public void WorldLabel_TwoStackedLabels_AClickStillPicksTheOneUnderThePointer()
    {
        _labelView.Labels = [Ground(800f, 400f, "Rusty Sword", 77L), Ground(800f, 400f, "Torch", 78L)];

        PressAndRelease(new Vector2(800f, 404f), BuildLabels);

        Assert.Equal([77L], _labelActions.PickedUp);
    }

    // BH-INPUT-1, the redirect half: the top label is looted by someone else while the button is
    // down, so the second label moves up into its place — under the pointer — and used to inherit its
    // index. The wrong item was then picked up. The click must be dropped instead.
    [Fact]
    public void WorldLabel_TheLabelUnderThePointerLeavingMidPress_PicksUpNothing()
    {
        _labelView.Labels = [Ground(800f, 400f, "Rusty Sword", 77L), Ground(800f, 400f, "Torch", 78L)];

        PressAndRelease(
            new Vector2(800f, 404f),
            BuildLabels,
            () => _labelView.Labels = [Ground(800f, 400f, "Torch", 78L)]);

        Assert.Empty(_labelActions.PickedUp);
    }

    // The same shape for an NPC: a quest giver must never be talked to because another one moved into
    // its slot.
    [Fact]
    public void WorldLabel_AnNpcLabelReplacedMidPress_TalksToNobody()
    {
        _labelView.Labels = [new HudWorldLabel(new Vector2(800f, 400f), "Old Mara", string.Empty, new HudLabelClick(HudLabelClickKind.InteractNpc, 900L))];

        PressAndRelease(
            new Vector2(800f, 404f),
            BuildLabels,
            () => _labelView.Labels = [new HudWorldLabel(new Vector2(800f, 400f), "Bren", string.Empty, new HudLabelClick(HudLabelClickKind.InteractNpc, 901L))]);

        Assert.Empty(_labelActions.Talked);
    }
}
