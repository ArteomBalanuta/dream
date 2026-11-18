using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Multiplayer;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-SOCIAL-3, below the network on a bare runtime: a trade's state was pushed once per change and never
// repeated, so a client that could not take that one push was left in a trade it could not see. StepTrades
// now also refreshes every TradeRules.RefreshIntervalTicks ticks while a trade lives.
//
// One runtime per test, and no more: a WorldSessionRuntime carries its own physics, and several of them
// standing up at once in the parallel pool starves the loopback tests that wait on a real host's progress.
public sealed class SocialTradeStateRefreshTests
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

    private static List<ServerTradeState> TradeStates(IEnumerable<SessionServerMessage> drained, PlayerSessionId session)
        => [.. drained.Where(entry => entry.SessionId == session).Select(entry => entry.Message).OfType<ServerTradeState>()];

    // Steps the trades over a tick range and returns the ticks on which the session was pushed a state.
    private static List<ulong> PushTicks(WorldSessionRuntime runtime, ulong from, ulong through, PlayerSessionId session)
    {
        List<ulong> ticks = [];
        for (ulong tick = from; tick <= through; tick++)
        {
            runtime.StepTrades(tick);
            if (TradeStates(Drain(runtime), session).Count > 0)
            {
                ticks.Add(tick);
            }
        }

        return ticks;
    }

    // Both variants from the packet, in one trade. A client that cannot take the one push (its settings
    // overlay is up) used to be left with a state it never saw: first an incoming request it never learned
    // of, then a stale lock state on a live trade. The cadence re-sends, so each drop self-corrects.
    //
    // The push ticks are asserted exactly: the cadence is driven by the server tick alone, so it lands on
    // the same ticks every run and in the same order, which the lockstep tests require.
    [Fact]
    public void ADroppedTradeStatePush_IsCorrectedByTheRefreshCadence_OnExactTicks()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        int cadence = TradeRules.RefreshIntervalTicks;
        Assert.True(Act(runtime, 10UL, SessionA, 1UL, new TradeRequestIntent(NameB)).Accepted);

        // Variant 1: the tick after the request sends the one dirty push, and the target's client throws it
        // away. Draining here stands in for a message delivered to a client that discarded it.
        runtime.StepTrades(11UL);
        Assert.Single(TradeStates(Drain(runtime), SessionB));

        // Nothing about the trade changes from here: no action, no dirty flag, so only the cadence can speak.
        List<ulong> requested = PushTicks(runtime, 12UL, 72UL, SessionB);
        Assert.Equal([(ulong)cadence, (ulong)(cadence * 2)], requested);

        // The refreshed state says what the dropped one did, so the client recovers in full.
        runtime.StepTrades((ulong)(cadence * 3));
        ServerTradeState refreshed = TradeStates(Drain(runtime), SessionB)[0];
        Assert.Equal(TradeStage.Incoming, refreshed.Stage);
        Assert.Equal(NameA, refreshed.Partner);

        // Variant 2: the trade is live now. Its state change is pushed once and dropped again.
        Assert.True(Act(runtime, 100UL, SessionB, 1UL, new TradeRespondIntent(Accept: true)).Accepted);
        runtime.StepTrades(101UL);
        Drain(runtime);

        List<ulong> open = PushTicks(runtime, 102UL, 160UL, SessionA);
        Assert.Equal([(ulong)(cadence * 4), (ulong)(cadence * 5)], open);
        runtime.StepTrades((ulong)(cadence * 6));
        Assert.Equal(TradeStage.Open, TradeStates(Drain(runtime), SessionA)[0].Stage);
        Assert.Equal(1, runtime.ActiveTradeCount);
    }

    // BH-SOCIAL-3 control: a normal trade still runs through request, accept and cancel; the cadence neither
    // ends it nor pushes a state every tick, and a closed trade is refreshed no further.
    [Fact]
    public void ANormalTrade_StillOpensAndCloses_WithTheCadenceRunning()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        int cadence = TradeRules.RefreshIntervalTicks;
        Assert.True(Act(runtime, 10UL, SessionA, 1UL, new TradeRequestIntent(NameB)).Accepted);
        runtime.StepTrades(11UL);
        Assert.Equal(TradeStage.Requested, TradeStates(Drain(runtime), SessionA)[0].Stage);

        Assert.True(Act(runtime, 12UL, SessionB, 1UL, new TradeRespondIntent(Accept: true)).Accepted);
        runtime.StepTrades(13UL);
        Assert.Equal(TradeStage.Open, TradeStates(Drain(runtime), SessionB)[0].Stage);

        // A whole cadence window of quiet ticks: one refresh, not one push a tick, and the trade is still on.
        int pushes = 0;
        for (ulong tick = 14UL; tick < 14UL + (ulong)cadence; tick++)
        {
            runtime.StepTrades(tick);
            pushes += TradeStates(Drain(runtime), SessionB).Count;
        }

        Assert.Equal(1, pushes);
        Assert.Equal(1, runtime.ActiveTradeCount);

        Assert.True(Act(runtime, 100UL, SessionA, 2UL, new TradeCancelIntent()).Accepted);
        Assert.Equal(0, runtime.ActiveTradeCount);
        Assert.Equal(TradeStage.Cancelled, TradeStates(Drain(runtime), SessionB)[^1].Stage);

        for (ulong tick = 101UL; tick < 101UL + (ulong)(cadence * 2); tick++)
        {
            runtime.StepTrades(tick);
        }

        Assert.Empty(TradeStates(Drain(runtime), SessionB));
    }
}
