using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Server;


public class DedicatedServerHostTickTests
{
    [Fact]
    public void RuntimeAttachedHost_AppliesQueuedCommandDuringAdvancedTick()
    {
        using DedicatedServerHost host = CreateHost(new FakeTickDrivenAuthority());

        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage("session-a", "player-a"));
        host.HandleClientMessage("connection-1", new PlayerCommandMessage(
            new PlayerCommandFrame(
                new PlayerSessionId("session-a"),
                4,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
                new PlayerMoveIntent(0f, 0f, 0f),
                new PlayerLookIntent(0f, 0f),
                PrimaryUsePressed: false)));

        int executedTicks = host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        ServerSnapshotMessage snapshot = host.CreateSnapshotMessage();

        Assert.Equal(1, executedTicks);
        Assert.Equal((ulong)1, snapshot.Snapshot.ServerTick);
        Assert.True(snapshot.Snapshot.LastAcceptedCommandSequences.TryGetValue("session-a", out ulong acceptedSequence));
        Assert.Equal((ulong)4, acceptedSequence);
    }

    [Fact]
    public void RuntimeAttachedHost_AdvancesTickWithoutFreshCommand()
    {
        using DedicatedServerHost host = CreateHost(new FakeTickDrivenAuthority());

        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage("session-a", "player-a"));

        int executedTicks = host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 2d);
        ServerSnapshotMessage snapshot = host.CreateSnapshotMessage();

        Assert.Equal(2, executedTicks);
        Assert.Equal((ulong)2, snapshot.Snapshot.ServerTick);
    }

    [Fact]
    public void RuntimeAttachedHost_PassesQueuedCommandsForAllJoinedSessionsIntoOneTick()
    {
        FakeTickDrivenAuthority authority = new();
        using DedicatedServerHost host = CreateHost(authority);

        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage("session-a", "player-a"));
        host.HandleClientMessage("connection-2", new ClientJoinRequestMessage("session-b", "player-b"));
        host.HandleClientMessage("connection-1", new PlayerCommandMessage(
            new PlayerCommandFrame(
                new PlayerSessionId("session-a"),
                4,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false)));
        host.HandleClientMessage("connection-2", new PlayerCommandMessage(
            new PlayerCommandFrame(
                new PlayerSessionId("session-b"),
                8,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false)));

        int executedTicks = host.AdvanceTime(SimulationClock.FixedDeltaSeconds);

        Assert.Equal(1, executedTicks);
        Assert.Collection(
            authority.LastTickCommands.OrderBy(frame => frame.SessionId.Value, StringComparer.Ordinal),
            first =>
            {
                Assert.Equal("session-a", first.SessionId.Value);
                Assert.Equal((ulong)4, first.Sequence);
            },
            second =>
            {
                Assert.Equal("session-b", second.SessionId.Value);
                Assert.Equal((ulong)8, second.Sequence);
            });
    }

    [Fact]
    public void RuntimeAttachedHost_QueuesCombatActionsAndDrainsThemOnTick()
    {
        FakeTickDrivenAuthority authority = new();
        using DedicatedServerHost host = CreateHost(authority);

        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage("session-a", "player-a"));
        IReadOnlyList<IServerToClientMessage> replies = host.HandleClientMessage(
            "connection-1",
            new PlayerActionMessage(
                new PlayerSessionId("session-a"),
                9,
                new CombatUseIntent(OpenTK.Mathematics.Vector3.Zero, -OpenTK.Mathematics.Vector3.UnitZ)));

        Assert.Empty(replies);
        Assert.Empty(authority.ProcessedActions);

        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);

        Assert.Single(authority.LastTickQueuedActions);
        QueuedPlayerAction queuedAction = authority.LastTickQueuedActions[0];
        Assert.Equal("session-a", queuedAction.SessionId.Value);
        Assert.Equal((ulong)9, queuedAction.Message.ActionSequence);
        Assert.Empty(authority.ProcessedActions);
    }

    [Fact]
    public async Task RuntimeAttachedHost_CanReceiveJoinsAndCommandsWhileTicking()
    {
        using DedicatedServerHost host = CreateHost(new FakeTickDrivenAuthority());

        Task[] clientTasks = Enumerable
            .Range(0, 16)
            .Select(clientIndex => Task.Run(() =>
            {
                string connectionId = $"connection-{clientIndex}";
                string sessionId = $"session-{clientIndex}";
                host.HandleClientMessage(
                    connectionId,
                    new ClientJoinRequestMessage(sessionId, $"player-{clientIndex}"));

                for (int sequence = 0; sequence < 1_000; sequence++)
                {
                    host.HandleClientMessage(
                        connectionId,
                        CreateCommandMessage(sessionId, (ulong)sequence));
                }
            }))
            .ToArray();
        Task tickTask = Task.Run(() =>
        {
            for (int iteration = 0; iteration < 2_000; iteration++)
            {
                host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
            }
        });

        await Task.WhenAll(clientTasks.Append(tickTask));
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

    private static PlayerCommandMessage CreateCommandMessage(string sessionId, ulong sequence)
    {
        return new PlayerCommandMessage(
            new PlayerCommandFrame(
                new PlayerSessionId(sessionId),
                sequence,
                PlayerModeSwitchIntent.None,
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false));
    }

    private sealed class FakeTickDrivenAuthority : ITickDrivenAuthoritativeSessionHost
    {
        private readonly Dictionary<string, ulong> _lastAcceptedCommandSequences = new(StringComparer.Ordinal);

        public List<PlayerCommandFrame> LastTickCommands { get; } = [];
        public List<QueuedPlayerAction> LastTickQueuedActions { get; } = [];
        public List<PlayerActionMessage> ProcessedActions { get; } = [];

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
        {
            return new JoinAcceptedMessage(requestedSessionId, 101, CurrentServerTick, InitialWorld: null);
        }

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message)
        {
            ProcessedActions.Add(message);
            return new AuthoritativeActionResult(
                message.SessionId.Value,
                message.ActionSequence,
                CurrentServerTick,
                Accepted: true,
                Payload: new WorldMutationResult(
                    WorldMutationKind.CubePlaced,
                    new OpenTK.Mathematics.Vector3(4f, 1f, 4f),
                    Applied: true));
        }

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false)
        {
            return new ServerSnapshotEnvelope(
                CurrentServerTick,
                _lastAcceptedCommandSequences,
                Array.Empty<ActorSnapshot>(),
                PlayerInteractionMode.Building,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>());
        }

        public IReadOnlyList<AuthoritativeActionResult> AdvanceTick(
            ulong serverTick,
            float deltaTime,
            IReadOnlyList<PlayerCommandFrame> commandFrames,
            IReadOnlyList<QueuedPlayerAction> queuedActions,
            PlayerSimulationTuning playerTuning,
            bool allowGameplaySimulation = true)
        {
            CurrentServerTick = serverTick;
            LastTickCommands.Clear();
            LastTickCommands.AddRange(commandFrames);
            LastTickQueuedActions.Clear();
            LastTickQueuedActions.AddRange(queuedActions);
            foreach (PlayerCommandFrame commandFrame in commandFrames)
            {
                _lastAcceptedCommandSequences[commandFrame.SessionId.Value] = commandFrame.Sequence;
            }

            return Array.Empty<AuthoritativeActionResult>();
        }

        public ulong CurrentServerTick { get; private set; }
    }
}
