using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

// M5: the trade window and the party panel. The window draws only with the inventory and a trade open, in
// the stage's shape (waiting, incoming, open), turns every click into one intent in the queue and nothing
// else, and stops editing the offer once either side locked. The party panel shows names and health and
// nothing without members.
public sealed class TradeWindowTests : IDisposable
{
    private readonly InventoryScene _scene = new();
    private readonly TradeHudState _trade = new();
    private readonly HudIntentQueue _intents = new();

    public void Dispose() => _scene.Dispose();

    private void Frame(HudIntentQueue? intents = null)
    {
        _scene.Canvas.Calls.Clear();
        TradeWindow.Build(_scene.Canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _scene.Actions, _trade, intents ?? _intents);
        _scene.Input.EndFrame();
    }

    private UiRect Units(float x, float y, float w, float h) =>
        _scene.Layout.FromUnits(_scene.Layout.Character, x, y, w, h);

    private void Click(UiRect rect, HudIntentQueue? intents = null)
    {
        _scene.Input.Pointer = InventoryScene.Centre(rect);
        _scene.Input.LeftClick = true;
        Frame(intents);
    }

    private static UiRect Dummy => default;

    private UiRect BagRow(int n) => Units(19f, 206f + (n * 13f), 152f, 12f);

    private UiRect GoldStep(int n)
    {
        float stepW = (152f - 10f) / 6f;
        return Units(19f + (n * (stepW + 2f)), 178f, stepW, 12f);
    }

    private UiRect Button(int n)
    {
        float third = (152f - 4f) / 3f;
        return Units(19f + (n * (third + 2f)), 316f, third, 16f);
    }

    private void AddBagItems(int count)
    {
        for (int n = 0; n < count; n++)
        {
            _scene.Items[n] = InventoryScene.Item(100 + n, "gem.amber");
        }

        _scene.Refresh();
    }

    private void SetGold(long gold) =>
        _scene.View.Inventory = _scene.View.Inventory!.Value with { Gold = gold };

    private static TradeHudLine Line(long id, string text, ItemRarity rarity = ItemRarity.Common) => new(id, rarity, text);

    private void Open(
        TradeHudStage stage = TradeHudStage.Open,
        TradeHudLine[]? mine = null,
        long myGold = 0,
        bool myLocked = false,
        TradeHudLine[]? theirs = null,
        long theirGold = 0,
        bool theirLocked = false,
        bool myConfirmed = false) =>
        _trade.Open(stage, "Bob the Bold", mine ?? [], myGold, myLocked, myConfirmed, theirs ?? [], theirGold, theirLocked, false);

    private TradeOfferIntent OnlyOffer() => Assert.IsType<TradeOfferIntent>(Dequeue());

    private IPlayerActionPayload Dequeue()
    {
        Assert.True(_intents.TryDequeue(out IPlayerActionPayload payload));
        Assert.Equal(0, _intents.Count);
        return payload;
    }

    [Fact]
    public void ItDrawsOnlyWithAnOpenTradeAndAnOpenInventory()
    {
        Frame();
        Assert.Empty(_scene.Canvas.Calls);

        Open();
        _scene.View.InventoryOpen = false;
        Frame();
        Assert.Empty(_scene.Canvas.Calls);

        _scene.View.InventoryOpen = true;
        Frame();
        Assert.True(_scene.Canvas.HasText("Bob the Bold"));
        Assert.True(_scene.Canvas.HasText("They offer"));
        Assert.True(_scene.Canvas.HasText("You offer"));
    }

    [Fact]
    public void AnIncomingRequest_OffersAcceptAndDecline()
    {
        Open(TradeHudStage.Incoming);
        Frame();
        Assert.True(_scene.Canvas.HasText("Accept"));
        Assert.True(_scene.Canvas.HasText("Decline"));

        Click(Units(19f, 90f, 152f, 16f));
        Assert.Equal(new TradeRespondIntent(true), Dequeue());
        Click(Units(19f, 110f, 152f, 16f));
        Assert.Equal(new TradeRespondIntent(false), Dequeue());
    }

    [Fact]
    public void AWaitingRequest_CanBeCancelled()
    {
        Open(TradeHudStage.Requested);
        Frame();
        Assert.True(_scene.Canvas.HasText("Waiting for an answer"));

        Click(Units(19f, 90f, 152f, 16f));
        Assert.IsType<TradeCancelIntent>(Dequeue());
    }

    [Fact]
    public void TheOpenWindow_ShowsTheirOfferAndBothGolds()
    {
        SetGold(300);
        Open(theirs: [Line(7, "Iron Band: Strength +1", ItemRarity.Uncommon)], theirGold: 45, myGold: 12);

        Frame();

        Assert.True(_scene.Canvas.HasText("Iron Band: Strength +1"));
        Assert.True(_scene.Canvas.HasText("45 gold"));
        Assert.True(_scene.Canvas.HasText("12 gold"));
        Assert.True(_scene.Canvas.HasText("Gold 300"));
        Assert.Equal(ItemTierStyle.Uncommon, _scene.Canvas.Texts.Last(t => t.Text == "Iron Band: Strength +1").Color);
    }

    [Fact]
    public void ClickingABagItem_PutsItInTheOffer_AndAgainTakesItOut()
    {
        AddBagItems(3);
        Open(myGold: 5);

        Click(BagRow(1));
        TradeOfferIntent add = OnlyOffer();
        Assert.Equal([101L], add.ItemInstanceIds);
        Assert.Equal(5, add.Gold);

        // The server confirms: the offer holds 101 and 102. A click on 101 takes it out.
        Open(mine: [Line(101, "Item 101"), Line(102, "Item 102")], myGold: 5);
        Click(BagRow(1));
        Assert.Equal([102L], OnlyOffer().ItemInstanceIds);
    }

    [Fact]
    public void AGoldStep_SendsTheWholeOfferWithTheNewGold_ClampedToTheWallet()
    {
        AddBagItems(1);
        SetGold(25);
        Open(mine: [Line(100, "Item 100")], myGold: 20);

        Click(GoldStep(4));
        TradeOfferIntent plus10 = OnlyOffer();
        Assert.Equal(25L, plus10.Gold);
        Assert.Equal([100L], plus10.ItemInstanceIds);

        // +100 cannot go past the wallet: clamped to 25, which is a change from 20 so it is sent.
        Click(GoldStep(5));
        Assert.Equal(25, OnlyOffer().Gold);

        // At the wallet there is nothing more to add: the step is dark.
        Open(mine: [Line(100, "Item 100")], myGold: 25);
        Click(GoldStep(3));
        Assert.Equal(0, _intents.Count);
        Click(GoldStep(2));
        Assert.Equal(24, OnlyOffer().Gold);
    }

    [Fact]
    public void OnceEitherSideLocked_TheOfferControlsDoNothing()
    {
        AddBagItems(2);
        SetGold(50);
        Open(mine: [Line(100, "Item 100")], myGold: 5, theirLocked: true, theirs: [Line(9, "Ring")]);

        Click(BagRow(1));
        Click(GoldStep(3));
        Assert.Equal(0, _intents.Count);

        Open(mine: [Line(100, "Item 100")], myGold: 5, myLocked: true);
        Click(BagRow(1));
        Click(GoldStep(3));
        Assert.Equal(0, _intents.Count);
    }

    [Fact]
    public void LockThenConfirm_AreOnePickEachAndConfirmWaitsForBothLocks()
    {
        Open(mine: [Line(100, "Item 100")]);
        Click(Button(1));
        Assert.Equal(0, _intents.Count);

        Click(Button(0));
        Assert.IsType<TradeLockIntent>(Dequeue());

        Open(mine: [Line(100, "Item 100")], myLocked: true);
        Click(Button(0));
        Click(Button(1));
        Assert.Equal(0, _intents.Count);

        Open(mine: [Line(100, "Item 100")], myLocked: true, theirLocked: true, theirs: [Line(9, "Ring")]);
        Click(Button(1));
        Assert.IsType<TradeConfirmIntent>(Dequeue());

        Open(mine: [Line(100, "Item 100")], myLocked: true, theirLocked: true, theirs: [Line(9, "Ring")], myConfirmed: true);
        Click(Button(1));
        Assert.Equal(0, _intents.Count);
    }

    [Fact]
    public void CancelAndTheCloseButton_BothCancelTheTrade()
    {
        Open();
        Click(Button(2));
        Assert.IsType<TradeCancelIntent>(Dequeue());
        Click(Units(19f, 395f, 24f, 24f));
        Assert.IsType<TradeCancelIntent>(Dequeue());
    }

    [Fact]
    public void WithoutAnIntentQueue_NothingIsClickable_AndNothingThrows()
    {
        AddBagItems(1);
        Open(TradeHudStage.Incoming);
        _scene.Canvas.Calls.Clear();
        TradeWindow.Build(_scene.Canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _scene.Actions, _trade, null);
        Open();
        _scene.Input.Pointer = InventoryScene.Centre(BagRow(0));
        _scene.Input.LeftClick = true;
        TradeWindow.Build(_scene.Canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _scene.Actions, _trade, null);
        Assert.Equal(0, _intents.Count);
    }

    [Fact]
    public void TheBagList_PagesAndSkipsWornItems()
    {
        AddBagItems(10);
        _scene.Equipped[0] = true;
        _scene.Refresh();
        Open();

        Frame();
        Assert.False(_scene.Canvas.HasText("Item 100"));
        Assert.True(_scene.Canvas.HasText("Item 101"));
        Assert.True(_scene.Canvas.HasText("1/2") || _scene.Canvas.HasText("Your goods"));
        Assert.Equal(9, TradeWindow.CountOfferable(_scene.View));

        _trade.BagPage = 1;
        Frame();
        Assert.True(_scene.Canvas.HasText("Item 109"));
        Assert.False(_scene.Canvas.HasText("Item 101"));

        _trade.BagPage = 99;
        Frame();
        Assert.Equal(1, _trade.BagPage);
    }

    [Fact]
    public void TheHudState_IsOpenWhileAStageIsShown_AndCloseClearsEverything()
    {
        Assert.False(_trade.IsOpen);
        Open(TradeHudStage.Open, mine: [Line(1, "a")], myGold: 4, theirs: [Line(2, "b")], theirGold: 6, myLocked: true);
        Assert.True(_trade.IsOpen);
        Assert.True(_trade.OffersItem(1));
        Assert.False(_trade.OfferEditable);

        _trade.Close();

        Assert.False(_trade.IsOpen);
        Assert.Equal((0, 0, 0L, 0L, false), (_trade.MyCount, _trade.TheirCount, _trade.MyGold, _trade.TheirGold, _trade.MyLocked));
        Assert.False(_trade.OffersItem(1));
    }

    [Fact]
    public void TheHudState_KeepsAtMostEightLines()
    {
        TradeHudLine[] many = [.. Enumerable.Range(1, 20).Select(n => Line(n, "x" + n))];
        _trade.Open(TradeHudStage.Open, "P", many, 0, false, false, many, 0, false, false);

        Assert.Equal(TradeHudState.MaxLines, _trade.MyCount);
        Assert.Equal(TradeHudState.MaxLines, _trade.TheirCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => _trade.MineAt(8));
    }
}

public sealed class PartyPanelTests : IDisposable
{
    private readonly InventoryScene _scene = new();
    private readonly PartyHudState _party = new();

    public void Dispose() => _scene.Dispose();

    private void Frame()
    {
        _scene.Canvas.Calls.Clear();
        PartyPanel.Build(_scene.Canvas, _scene.View, _scene.Layout, _party, new HudIntentQueue());
    }

    [Fact]
    public void ItDrawsNothing_WithoutMembersOrAnInvite_AndWithoutAState()
    {
        Frame();
        Assert.Empty(_scene.Canvas.Calls);

        PartyPanel.Build(_scene.Canvas, _scene.View, _scene.Layout, null, null);
        Assert.Empty(_scene.Canvas.Calls);
    }

    [Fact]
    public void MembersShowNameLevelAndAHealthBar_TheLeaderInGold()
    {
        _party.Set(
            [
                new PartyMemberLine("Alice", 7, 30, 60, IsLeader: true),
                new PartyMemberLine("Bob", 3, 0, 40, IsLeader: false),
            ],
            null);

        Frame();

        Assert.True(_scene.Canvas.HasText("Alice"));
        Assert.True(_scene.Canvas.HasText("Bob"));
        Assert.True(_scene.Canvas.HasText("7"));
        Assert.NotEqual(
            _scene.Canvas.Texts.Last(t => t.Text == "Alice").Color,
            _scene.Canvas.Texts.Last(t => t.Text == "Bob").Color);
        Assert.Equal(2, _party.Count);
    }

    [Fact]
    public void APendingInvite_IsShownEvenWithoutAParty_AndNamesTheCommands()
    {
        _party.Set([], "Carol");

        Frame();

        Assert.True(_scene.Canvas.HasText("Carol"));
        Assert.Contains(_scene.Canvas.Texts, t => t.Text.ToString().Contains("/accept", StringComparison.Ordinal));
        Assert.False(_party.IsEmpty);
    }

    [Fact]
    public void TheState_KeepsFiveMembers_AndClearEmptiesIt()
    {
        PartyMemberLine[] seven = [.. Enumerable.Range(0, 7).Select(n => new PartyMemberLine("m" + n, 1, 1, 1, n == 0))];
        _party.Set(seven, "x");

        Assert.Equal(PartyHudState.MaxMembers, _party.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => _party.MemberAt(5));

        _party.Clear();
        Assert.True(_party.IsEmpty);
    }
}
