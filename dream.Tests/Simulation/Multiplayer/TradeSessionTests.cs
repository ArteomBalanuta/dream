using Dream.Simulation.Inventory;
using Dream.Simulation.Multiplayer;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.Multiplayer;

// M5: the pure trade state machine and its offer rules. Requested, Open, both lock, both confirm; an offer
// edit after a lock is the caller's cue to cancel; hostile offers are refused before any lookup.
public sealed class TradeSessionTests
{
    private static TradeSession Open()
    {
        TradeSession session = new("alice", "bob", 10);
        Assert.Equal(TradeStepOutcome.Ok, session.Accept("bob"));
        return session;
    }

    [Fact]
    public void ARequest_IsOpenedOnlyByTheTarget_AndOnlyOnce()
    {
        TradeSession session = new("alice", "bob", 10);

        Assert.Equal(TradePhase.Requested, session.Phase);
        Assert.Equal(TradeStepOutcome.NotInTrade, session.Accept("alice"));
        Assert.Equal(TradeStepOutcome.NotInTrade, session.Accept("carol"));
        Assert.Equal(TradeStepOutcome.WrongPhase, session.SetOffer("alice", [1], 0));
        Assert.Equal(TradeStepOutcome.Ok, session.Accept("BOB"));
        Assert.Equal(TradePhase.Open, session.Phase);
        Assert.Equal(TradeStepOutcome.WrongPhase, session.Accept("bob"));
        Assert.Equal(0, session.SideOf("ALICE"));
        Assert.Equal(1, session.SideOf("bob"));
        Assert.Equal(-1, session.SideOf("carol"));
    }

    [Fact]
    public void OffersAreEditedFreelyBeforeAnyLock_AndReplaceTheWholeOffer()
    {
        TradeSession session = Open();

        Assert.Equal(TradeStepOutcome.Ok, session.SetOffer("alice", [1, 2], 5));
        Assert.Equal(TradeStepOutcome.Ok, session.SetOffer("alice", [3], 7));
        Assert.Equal([3L], session.Items(0));
        Assert.Equal(7, session.Gold(0));
        Assert.Empty(session.Items(1));
    }

    [Fact]
    public void AnEditAfterEitherLock_IsRefusedAsEditAfterLock()
    {
        TradeSession session = Open();
        session.SetOffer("alice", [1], 0);
        Assert.Equal(TradeStepOutcome.Ok, session.Lock("alice"));

        Assert.Equal(TradeStepOutcome.EditAfterLock, session.SetOffer("alice", [2], 0));
        Assert.Equal(TradeStepOutcome.EditAfterLock, session.SetOffer("bob", [9], 0));
        Assert.Equal([1L], session.Items(0));
    }

    [Fact]
    public void ConfirmNeedsBothLocks_AndADoubleConfirmIsRefused_AndTheSecondConfirmCommits()
    {
        TradeSession session = Open();
        session.SetOffer("alice", [1], 0);
        session.SetOffer("bob", [], 10);

        Assert.Equal(TradeStepOutcome.NotLocked, session.Confirm("alice"));
        session.Lock("alice");
        Assert.Equal(TradeStepOutcome.NotLocked, session.Confirm("alice"));
        session.Lock("bob");

        Assert.Equal(TradeStepOutcome.Ok, session.Confirm("alice"));
        Assert.Equal(TradeStepOutcome.AlreadyConfirmed, session.Confirm("alice"));
        Assert.False(session.BothConfirmed);
        Assert.Equal(TradeStepOutcome.Ok, session.Confirm("bob"));
        Assert.True(session.BothConfirmed);
    }

    [Fact]
    public void ANothingOfferedTrade_CannotBeLocked()
    {
        TradeSession session = Open();

        Assert.Equal(TradeStepOutcome.NothingOffered, session.Lock("alice"));
        session.SetOffer("bob", [], 1);
        Assert.Equal(TradeStepOutcome.Ok, session.Lock("alice"));
    }

    [Fact]
    public void AClosedTrade_AcceptsNothing()
    {
        TradeSession session = Open();
        session.Close();

        Assert.Equal(TradeStepOutcome.WrongPhase, session.SetOffer("alice", [1], 0));
        Assert.Equal(TradeStepOutcome.WrongPhase, session.Lock("alice"));
        Assert.Equal(TradeStepOutcome.WrongPhase, session.Confirm("alice"));
    }

    [Fact]
    public void HostileOffers_AreRefusedAsBadOffers_BeforeAnythingIsKept()
    {
        TradeSession session = Open();
        long[] nine = [1, 2, 3, 4, 5, 6, 7, 8, 9];

        Assert.Equal(TradeStepOutcome.BadOffer, session.SetOffer("alice", null, 0));
        Assert.Equal(TradeStepOutcome.BadOffer, session.SetOffer("alice", nine, 0));
        Assert.Equal(TradeStepOutcome.BadOffer, session.SetOffer("alice", [1, 1], 0));
        Assert.Equal(TradeStepOutcome.BadOffer, session.SetOffer("alice", [0], 0));
        Assert.Equal(TradeStepOutcome.BadOffer, session.SetOffer("alice", [-4], 0));
        Assert.Equal(TradeStepOutcome.BadOffer, session.SetOffer("alice", [], -1));
        Assert.Equal(TradeStepOutcome.BadOffer, session.SetOffer("alice", [], InventoryConstants.MaxGold + 1));
        Assert.Empty(session.Items(0));
        Assert.Equal(0, session.Gold(0));
        Assert.Equal(TradeStepOutcome.Ok, session.SetOffer("alice", [1, 2, 3, 4, 5, 6, 7, 8], InventoryConstants.MaxGold));
    }

    [Fact]
    public void Range_IsHorizontalOnly_AtSixMetres()
    {
        Assert.True(TradeRules.InRange(new Vector3(0, 0, 0), new Vector3(6, 50, 0)));
        Assert.True(TradeRules.InRange(new Vector3(0, 0, 0), new Vector3(3.5f, 0, 4.8f)));
        Assert.False(TradeRules.InRange(new Vector3(0, 0, 0), new Vector3(6.01f, 0, 0)));
        Assert.Equal(6f, TradeRules.Range);
        Assert.Equal(8, TradeRules.MaxItems);
    }
}
