using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-SOCIAL-5, below the network on a bare runtime: trade.request had no mute check, the gap the sibling
// party.invite had already closed. Two seats attached at the world spawn, so they are in trade range.
//
// One runtime per test, and no more: a WorldSessionRuntime carries its own physics, and several of them
// standing up at once in the parallel pool starves the loopback tests that wait on a real host's progress.
public sealed class SocialTradeRequestMuteTests
{
    private const string NameA = "Social Seat A";
    private const string NameB = "Social Seat B";
    private static readonly PlayerSessionId SessionA = new("social-a");
    private static readonly PlayerSessionId SessionB = new("social-b");

    private static WorldSessionRuntime CreateRuntime()
    {
        WorldSessionRuntime runtime = new(
            WorldDefaults.StartupFloorSize,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub());
        runtime.WriteCheckpointOnDispose = false;
        runtime.AttachRemoteSession(SessionA, NameA);
        runtime.AttachRemoteSession(SessionB, NameB);
        return runtime;
    }

    private static AuthoritativeActionResult Act(
        WorldSessionRuntime runtime,
        ulong tick,
        PlayerSessionId session,
        ulong sequence,
        IPlayerActionPayload payload)
    {
        PlayerActionMessage message = new(session, sequence, payload);
        return runtime.ProcessQueuedAuthoritativeActions(
            tick,
            [new QueuedPlayerAction(sequence, session, message)])[0];
    }

    // The production drain shape: a predicate, so the per-session counts follow the delivery one entry at a
    // time (the predicate-free overload clears every count, which only tests reach).
    private static List<SessionServerMessage> Drain(WorldSessionRuntime runtime)
    {
        List<SessionServerMessage> drained = [];
        runtime.DrainSessionMessagesInto(drained, _ => true);
        return drained;
    }

    private static List<string> ChatTexts(IEnumerable<SessionServerMessage> drained, PlayerSessionId session)
        => [.. drained
            .Where(entry => entry.SessionId == session && entry.Message is ServerChatMessage)
            .Select(entry => ((ServerChatMessage)entry.Message).Text)];

    private static List<ServerTradeState> TradeStates(IEnumerable<SessionServerMessage> drained, PlayerSessionId session)
        => [.. drained.Where(entry => entry.SessionId == session).Select(entry => entry.Message).OfType<ServerTradeState>()];

    // A muted account cannot put a trade notice on a named player's screen, the rule party.invite already
    // states. With the control in the same runtime: an unmuted player still asks, a muted player may still
    // answer a request made to it (the mute takes reach away, not play), and with nobody muted the request
    // path is exactly as it was.
    [Fact]
    public void AMutedSender_CannotReachANamedPlayerWithATradeRequest_AndAnUnmutedOneStillCan()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.IsSessionMuted = session => session == SessionA;

        Assert.False(Act(runtime, 10UL, SessionA, 1UL, new TradeRequestIntent(NameB)).Accepted);

        Assert.Equal(0, runtime.ActiveTradeCount);
        List<SessionServerMessage> drained = Drain(runtime);
        Assert.Empty(TradeStates(drained, SessionB));
        Assert.Contains("You are muted.", ChatTexts(drained, SessionA));
        // The mute refused it before the cooldown could be spent, so nothing was charged to the sender.
        Assert.Equal(0, runtime.TradeRequestCooldownRefusals);

        // The other seat is not muted and still asks.
        Assert.True(Act(runtime, 11UL, SessionB, 1UL, new TradeRequestIntent(NameA)).Accepted);
        runtime.StepTrades(12UL);
        Assert.Equal(TradeStage.Incoming, TradeStates(Drain(runtime), SessionA)[0].Stage);

        // The muted seat still answers a request made to it.
        Assert.True(Act(runtime, 13UL, SessionA, 2UL, new TradeRespondIntent(Accept: true)).Accepted);
        runtime.StepTrades(14UL);
        Assert.Equal(TradeStage.Open, TradeStates(Drain(runtime), SessionB)[0].Stage);

        // Nobody muted: the request path is untouched.
        runtime.IsSessionMuted = null;
        Assert.True(Act(runtime, 15UL, SessionA, 3UL, new TradeCancelIntent()).Accepted);
        Assert.True(Act(runtime, 16UL, SessionA, 4UL, new TradeRequestIntent(NameB)).Accepted);
        Assert.Equal(1, runtime.ActiveTradeCount);
    }
}
