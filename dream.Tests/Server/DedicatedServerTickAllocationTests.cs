using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Server;

// The dedicated server runs the host tick pipeline 60 times a second. Before MEM-4, with two
// joined sessions and no input, it allocated (bytes per call): ActionQueue.Drain 776,
// CommandInbox.Drain 72, DedicatedServerHost.AdvanceTime 1256, DrainPendingActionResults 32,
// DedicatedServerService.AdvanceOnceAsync 1728 unjoined / 608 without a tick / 2976 joined, and
// RuntimeAuthoritativeSessionHost.AdvanceTick 2352 against 1950 for the runtime tick it wraps
// (Debug build).
public class DedicatedServerTickAllocationTests
{
#if DEBUG
    // Debug builds compile async methods to class state machines, so each AdvanceOnceAsync call
    // allocates two of them (232 B measured) even when it completes synchronously. Release: 0 B.
    private const long AsyncStateMachineBudgetBytes = 256;
#else
    private const long AsyncStateMachineBudgetBytes = 0;
#endif

    // The joined path still allocates the snapshot message it broadcasts (168 B; it is consumed
    // asynchronously) and each connection's outbound queue entry or coalesced snapshot inside
    // ServerConnection, which this task does not own. Release measured 856 B; the fake authority adds nothing.
    private const long JoinedServiceTickBudgetBytes = 1024 + AsyncStateMachineBudgetBytes;

    // The runtime host layer may add nothing on top of the runtime tick it wraps. Since MEM-11
    // the idle runtime tick itself allocates nothing (the per-player collision report was its
    // last allocation), so there is no run-to-run variation left to absorb: both are 0 B.
    private const long RuntimeTickBudgetBytes = 0;
    private const long RuntimeHostOverheadBudgetBytes = 0;

    private static readonly PlayerSessionId SessionA = new("session-a");
    private static readonly PlayerSessionId SessionB = new("session-b");

    private readonly ITestOutputHelper _output;

    public DedicatedServerTickAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TickRunner_StatefulAdvance_AllocatesNothing()
    {
        ServerTickRunner runner = new();
        TickCounter counter = new();

        long bytes = AllocationAssert.AtMost(
            0,
            () => runner.Advance(
                SimulationClock.FixedDeltaSeconds,
                counter,
                static (state, tick, fixedDeltaSeconds) => state.Count++));

        _output.WriteLine($"ServerTickRunner.Advance<TState>: {bytes} B");
        Assert.True(counter.Count > 0);
    }

    [Fact]
    public void CommandInbox_DrainInto_AllocatesNothing()
    {
        LatestPlayerCommandInbox inbox = new();
        List<PlayerSessionId> sessions = [SessionA, SessionB];
        List<PlayerCommandFrame> drained = [];
        ulong sequence = 0;

        long emptyBytes = AllocationAssert.AtMost(0, () => inbox.DrainInto(sessions, drained));
        long pendingBytes = AllocationAssert.AtMost(
            0,
            () =>
            {
                sequence++;
                inbox.Enqueue(CreateCommand(SessionA, sequence));
                inbox.Enqueue(CreateCommand(SessionB, sequence));
                inbox.DrainInto(sessions, drained);
            });

        _output.WriteLine($"LatestPlayerCommandInbox.DrainInto: {emptyBytes} B empty, {pendingBytes} B with two frames");
        Assert.Equal(2, drained.Count);
    }

    [Fact]
    public void CommandInbox_DrainInto_ReturnsFramesInSessionOrderAndTryDrainTakesOne()
    {
        LatestPlayerCommandInbox inbox = new();
        inbox.Enqueue(CreateCommand(SessionA, 3));
        inbox.Enqueue(CreateCommand(SessionB, 5));
        List<PlayerCommandFrame> drained = [CreateCommand(SessionA, 99)];

        inbox.DrainInto([SessionB, new PlayerSessionId("session-c")], drained);

        PlayerCommandFrame frame = Assert.Single(drained);
        Assert.Equal(SessionB, frame.SessionId);
        Assert.Equal((ulong)5, frame.Sequence);
        Assert.False(inbox.TryDrain(SessionB, out _));
        Assert.True(inbox.TryDrain(SessionA, out PlayerCommandFrame remaining));
        Assert.Equal((ulong)3, remaining.Sequence);
        Assert.False(inbox.HasPending);
    }

    [Fact]
    public void ActionQueue_DrainInto_AllocatesNothing()
    {
        PlayerActionQueue queue = new();
        List<PlayerSessionId> sessions = [SessionA, SessionB];
        List<QueuedPlayerAction> drained = [];
        PlayerActionMessage actionA = CreateAction(SessionA, 1);
        PlayerActionMessage actionB = CreateAction(SessionB, 2);
        PlayerSessionId sessionC = new("session-c");
        List<PlayerSessionId> laterSessions = [sessionC];
        List<QueuedPlayerAction> drainedLater = [];
        PlayerActionMessage unjoinedAction = CreateAction(sessionC, 3);

        long emptyBytes = AllocationAssert.AtMost(0, () => queue.DrainInto(sessions, drained));
        long pendingBytes = AllocationAssert.AtMost(
            0,
            () =>
            {
                queue.Enqueue(actionB);
                queue.Enqueue(unjoinedAction);
                queue.Enqueue(actionA);
                queue.DrainInto(sessions, drained);
                queue.DrainInto(laterSessions, drainedLater);
            });

        _output.WriteLine($"PlayerActionQueue.DrainInto: {emptyBytes} B empty, {pendingBytes} B with joined and unjoined actions");
        Assert.Collection(
            drained,
            first => Assert.Same(actionB, first.Message),
            second => Assert.Same(actionA, second.Message));
        Assert.Same(unjoinedAction, Assert.Single(drainedLater).Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1234)]
    public void ActionQueue_DrainOrder_MatchesOriginalLinqOrder(int seed)
    {
        Random random = new(seed);
        PlayerSessionId[] sessionPool =
        [
            new("session-a"),
            new("session-b"),
            new("Session-A"),
            new("session-c"),
            new("s")
        ];
        PlayerActionQueue drainQueue = new();
        PlayerActionQueue drainIntoQueue = new();
        List<QueuedPlayerAction> oraclePending = [];
        List<QueuedPlayerAction> drainedInto = [];

        for (int step = 0; step < 400; step++)
        {
            if (random.Next(3) != 0)
            {
                PlayerActionMessage message = CreateAction(
                    sessionPool[random.Next(sessionPool.Length)],
                    (ulong)random.Next(4));
                QueuedPlayerAction queued = drainQueue.Enqueue(message);
                Assert.Equal(queued, drainIntoQueue.Enqueue(message));
                oraclePending.Add(queued);
                continue;
            }

            PlayerSessionId[] joined = Enumerable
                .Range(0, random.Next(sessionPool.Length + 2))
                .Select(_ => sessionPool[random.Next(sessionPool.Length)])
                .ToArray();
            QueuedPlayerAction[] expected = OriginalLinqDrain(oraclePending, joined);

            IReadOnlyList<QueuedPlayerAction> drained = drainQueue.Drain(joined);
            drainIntoQueue.DrainInto(joined, drainedInto);

            Assert.Equal(expected, drained);
            Assert.Equal(expected, drainedInto);
        }

        PlayerSessionId[] everyone = sessionPool;
        Assert.Equal(OriginalLinqDrain(oraclePending, everyone), drainQueue.Drain(everyone));
    }

    [Fact]
    public void Host_AdvanceTime_WithTwoJoinedSessionsAndNoInput_AllocatesNothing()
    {
        QuietAuthority authority = new();
        using DedicatedServerHost host = CreateHost(authority);
        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage("session-a", "player-a"));
        host.HandleClientMessage("connection-2", new ClientJoinRequestMessage("session-b", "player-b"));
        List<AuthoritativeActionResult> results = [];

        long advanceBytes = AllocationAssert.AtMost(0, () => host.AdvanceTime(SimulationClock.FixedDeltaSeconds));
        long drainBytes = AllocationAssert.AtMost(0, () => host.DrainPendingActionResultsInto(results));

        _output.WriteLine($"DedicatedServerHost.AdvanceTime: {advanceBytes} B; DrainPendingActionResultsInto: {drainBytes} B");
        Assert.True(authority.TickCount > 0);
    }

    [Fact]
    public void Host_JoinedSessions_FollowConnectionAndJoinChangesAfterFirstTick()
    {
        QuietAuthority authority = new();
        using DedicatedServerHost host = CreateHost(authority);
        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage("session-a", "player-a"));
        host.HandleClientMessage("connection-2", new ClientHelloMessage("player-b"));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);

        host.HandleClientMessage("connection-2", new ClientJoinRequestMessage("session-b", "player-b"));
        host.HandleClientMessage("connection-3", new ClientJoinRequestMessage("session-c", "player-c"));
        host.HandleClientMessage("connection-1", new PlayerCommandMessage(CreateCommand(SessionA, 1)));
        host.HandleClientMessage("connection-2", new PlayerCommandMessage(CreateCommand(SessionB, 1)));
        host.HandleClientMessage("connection-3", new PlayerCommandMessage(CreateCommand(new PlayerSessionId("session-c"), 1)));
        host.ReleaseConnection("connection-1");
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);

        Assert.Equal(
            ["session-b", "session-c"],
            authority.LastCommandSessions.OrderBy(session => session, StringComparer.Ordinal));
    }

    [Fact]
    public void Host_DrainPendingActionResults_StillReturnsAFreshList()
    {
        using DedicatedServerHost host = CreateHost(new QuietAuthority());

        IReadOnlyList<AuthoritativeActionResult> first = host.DrainPendingActionResults();
        IReadOnlyList<AuthoritativeActionResult> second = host.DrainPendingActionResults();

        Assert.Empty(first);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Service_AdvanceOnce_AllocatesNothingWithoutJoinedConnections()
    {
        DedicatedServerHost host = CreateHost(new QuietAuthority());
        await using DedicatedServerService service = new(host, new NullServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer());
        AttachConnection(service, host, "connection-1");
        AttachConnection(service, host, "connection-2");

        long unjoinedBytes = AllocationAssert.MeasureBytesPerIteration(
            () => service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds).GetAwaiter().GetResult());
        long noTickBytes = AllocationAssert.MeasureBytesPerIteration(
            () => service.AdvanceOnceAsync(0d).GetAwaiter().GetResult());

        _output.WriteLine(
            $"DedicatedServerService.AdvanceOnceAsync: {unjoinedBytes} B unjoined, {noTickBytes} B without a tick " +
            $"(diagnostics {(NetworkDiagnosticsLog.IsEnabled ? "on" : "off")})");
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            Assert.True(unjoinedBytes <= AsyncStateMachineBudgetBytes, $"Unjoined tick allocated {unjoinedBytes} B.");
            Assert.True(noTickBytes <= AsyncStateMachineBudgetBytes, $"Call without a tick allocated {noTickBytes} B.");
        }
    }

    [Fact]
    public async Task Service_AdvanceOnce_WithTwoJoinedConnections_StaysWithinBudget()
    {
        DedicatedServerHost host = CreateHost(new QuietAuthority());
        await using DedicatedServerService service = new(host, new NullServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer());
        AttachConnection(service, host, "connection-1");
        AttachConnection(service, host, "connection-2");
        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage("session-a", "player-a"));
        host.HandleClientMessage("connection-2", new ClientJoinRequestMessage("session-b", "player-b"));

        long joinedBytes = AllocationAssert.MeasureBytesPerIteration(
            () => service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds).GetAwaiter().GetResult());
        long snapshotMessageBytes = AllocationAssert.MeasureBytesPerIteration(() => host.CreateSnapshotMessage());

        _output.WriteLine(
            $"DedicatedServerService.AdvanceOnceAsync: {joinedBytes} B with two joined connections, of which " +
            $"{snapshotMessageBytes} B is the snapshot message and the rest the two connections' outbound queues");
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            Assert.True(
                joinedBytes <= JoinedServiceTickBudgetBytes,
                $"Allocated {joinedBytes} B per tick; budget is {JoinedServiceTickBudgetBytes} B.");
        }
    }

    [Fact]
    public void RuntimeHost_AdvanceTick_AddsLittleOverRuntimeTick()
    {
        // The 0 B budgets hold only with diagnostics off: Player.CheckAndLogCollisions allocates its
        // collision report while DREAM_NET_DIAGNOSTICS is set. Fail loudly instead of with a byte count.
        Assert.False(
            NetworkDiagnosticsLog.IsEnabled,
            "This test needs DREAM_NET_DIAGNOSTICS unset: the diagnostics collision report allocates, so the 0 B runtime tick budget does not apply.");
        using RuntimeAuthoritativeSessionHost runtimeHost = RuntimeAuthoritativeSessionHost.CreateDefault();
        runtimeHost.JoinSession("session-a", "Player A");
        runtimeHost.JoinSession("session-b", "Player B");
        List<PlayerCommandFrame> noCommands = [];
        List<QueuedPlayerAction> noActions = [];
        ulong tick = 0;

        // Same default frame the host forwards, so the runtime takes the same accepted-command path.
        PlayerCommandFrame defaultFrame = default;
        long runtimeBytes = long.MaxValue;
        long hostBytes = long.MaxValue;

        // Interleave the windows and keep the minimum of 3 per side so a one-off growth
        // (list resize, enemy or physics state) in a single window cannot decide the result.
        for (int round = 0; round < 3; round++)
        {
            long runtimeRound = AllocationAssert.MeasureBytesPerIteration(
                () => runtimeHost.Runtime.AdvanceAuthoritativeTick(
                    ++tick,
                    SimulationClock.FixedDeltaSeconds,
                    defaultFrame,
                    PlayerSimulationTuning.AuthoritativeDefault),
                iterations: 100,
                warmupIterations: 20);
            long hostRound = AllocationAssert.MeasureBytesPerIteration(
                () => runtimeHost.AdvanceTick(
                    ++tick,
                    SimulationClock.FixedDeltaSeconds,
                    noCommands,
                    noActions,
                    PlayerSimulationTuning.AuthoritativeDefault),
                iterations: 100,
                warmupIterations: 20);
            runtimeBytes = Math.Min(runtimeBytes, runtimeRound);
            hostBytes = Math.Min(hostBytes, hostRound);
        }

        _output.WriteLine($"RuntimeAuthoritativeSessionHost.AdvanceTick: {hostBytes} B; WorldSessionRuntime.AdvanceAuthoritativeTick: {runtimeBytes} B");
        Assert.True(
            runtimeBytes <= RuntimeTickBudgetBytes,
            $"Runtime tick allocated {runtimeBytes} B; budget is {RuntimeTickBudgetBytes} B.");
        Assert.True(
            hostBytes - runtimeBytes <= RuntimeHostOverheadBudgetBytes,
            $"Host tick allocated {hostBytes} B against {runtimeBytes} B for the runtime tick; overhead budget is {RuntimeHostOverheadBudgetBytes} B.");
    }

    // The drain order the queue used before MEM-4, kept as the oracle.
    private static QueuedPlayerAction[] OriginalLinqDrain(List<QueuedPlayerAction> pending, IEnumerable<PlayerSessionId> joinedSessionIds)
    {
        HashSet<PlayerSessionId> joined = joinedSessionIds.ToHashSet();
        QueuedPlayerAction[] drained = pending
            .Where(action => joined.Contains(action.SessionId))
            .OrderBy(action => action.ReceiveOrder)
            .ThenBy(action => action.SessionId.Value, StringComparer.Ordinal)
            .ThenBy(action => action.Message.ActionSequence)
            .ToArray();

        pending.RemoveAll(action => joined.Contains(action.SessionId));
        return drained;
    }

    private static DedicatedServerHost CreateHost(ITickDrivenAuthoritativeSessionHost authority)
    {
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(authority, router);
    }

    private static void AttachConnection(DedicatedServerService service, DedicatedServerHost host, string connectionId)
    {
        service.AttachConnection(new ServerConnection(
            new NullTransportConnection(connectionId),
            host,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer()));
    }

    private static PlayerCommandFrame CreateCommand(PlayerSessionId sessionId, ulong sequence)
    {
        return new PlayerCommandFrame(
            sessionId,
            sequence,
            PlayerModeSwitchIntent.None,
            PlayerMoveIntent.None,
            PlayerLookIntent.None,
            PrimaryUsePressed: false);
    }

    private static PlayerActionMessage CreateAction(PlayerSessionId sessionId, ulong actionSequence)
    {
        return new PlayerActionMessage(
            sessionId,
            actionSequence,
            new CombatUseIntent(OpenTK.Mathematics.Vector3.Zero, -OpenTK.Mathematics.Vector3.UnitZ));
    }

    private sealed class TickCounter
    {
        public int Count { get; set; }
    }

    // Allocation-free authority, so measurements show only the host and service pipeline.
    private sealed class QuietAuthority : ITickDrivenAuthoritativeSessionHost
    {
        private readonly ServerSnapshotEnvelope _snapshot = new(
            0,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        public int TickCount { get; private set; }

        public List<string> LastCommandSessions { get; } = [];

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
        {
            return new JoinAcceptedMessage(requestedSessionId, 101, 0, InitialWorld: null);
        }

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message)
        {
            throw new NotSupportedException();
        }

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false)
        {
            return _snapshot;
        }

        public IReadOnlyList<AuthoritativeActionResult> AdvanceTick(
            ulong serverTick,
            float deltaTime,
            IReadOnlyList<PlayerCommandFrame> commandFrames,
            IReadOnlyList<QueuedPlayerAction> queuedActions,
            PlayerSimulationTuning playerTuning,
            bool allowGameplaySimulation = true)
        {
            TickCount++;
            LastCommandSessions.Clear();
            for (int i = 0; i < commandFrames.Count; i++)
            {
                LastCommandSessions.Add(commandFrames[i].SessionId.Value);
            }

            return Array.Empty<AuthoritativeActionResult>();
        }
    }

    private sealed class NullServerTransport : IServerTransport
    {
        public int Port => 5057;

        public void Start()
        {
        }

        public ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullTransportConnection : ITransportConnection
    {
        public NullTransportConnection(string connectionId)
        {
            ConnectionId = connectionId;
        }

        public string ConnectionId { get; }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(0);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
