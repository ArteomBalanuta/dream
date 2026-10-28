using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Multiplayer.Protocol;


public class ProtocolWiringTests
{
    [Fact]
    public void DedicatedServerHost_HandleClientMessage_UsesProtocolRouterAndProducesReply()
    {
        DedicatedServerHost host = DedicatedServerHost.CreateDefault();

        IReadOnlyList<IServerToClientMessage> replies = host.HandleClientMessage(
            "connection-1",
            new ClientJoinRequestMessage("session-a", "player-a"));

        JoinRejectedMessage rejection = Assert.IsType<JoinRejectedMessage>(Assert.Single(replies));
        Assert.Contains("not implemented", rejection.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClientProtocolSession_ApplyServerMessage_CapturesLatestSnapshot()
    {
        ClientProtocolSession session = ClientProtocolSession.CreateDefault();
        ServerSnapshotEnvelope snapshot = new(
            21,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        session.ApplyServerMessage(new ServerSnapshotMessage(snapshot));

        Assert.NotNull(session.Context.LastSnapshot);
        Assert.Equal((ulong)21, session.Context.LastSnapshot?.ServerTick);
    }

    [Fact]
    public void DedicatedServerHost_ReusesPerConnectionRoutingContextAcrossMessages()
    {
        DedicatedServerHost host = CreateHost(new FakeAuthoritativeSession());

        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage("session-a", "player-a"));
        host.HandleClientMessage("connection-1", new PlayerCommandMessage(
            new PlayerCommandFrame(
                new PlayerSessionId("session-a"),
                7,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
                new PlayerMoveIntent(1f, 0f, 0f),
                new PlayerLookIntent(0f, 0f),
                PrimaryUsePressed: false)));

        Assert.True(host.TryGetConnectionContext("connection-1", out ServerRoutingContext? context));
        Assert.NotNull(context);
        Assert.NotNull(context.LastJoinRequest);
        Assert.NotNull(context.LastCommandMessage);
        Assert.Equal((ulong)7, context.LastCommandMessage?.CommandFrame.Sequence);
    }

    [Fact]
    public void PlayerCommandHandler_DoesNotEnqueueCommandBeforeJoin()
    {
        RecordingPlayerCommandInbox inbox = new();
        ServerRoutingContext context = new("connection-1", commandInbox: inbox);
        PlayerCommandHandler handler = new();

        handler.Handle(context, new PlayerCommandMessage(CreateCommandFrame("session-a", 1)));

        Assert.Empty(inbox.EnqueuedFrames);
        Assert.NotNull(context.LastCommandMessage);
    }

    [Fact]
    public void PlayerCommandHandler_DoesNotEnqueueCommandForAnotherSession()
    {
        RecordingPlayerCommandInbox inbox = new();
        ServerRoutingContext context = new("connection-1", commandInbox: inbox)
        {
            LastJoinRequest = new ClientJoinRequestMessage("session-a", "player-a")
        };
        PlayerCommandHandler handler = new();

        handler.Handle(context, new PlayerCommandMessage(CreateCommandFrame("session-b", 2)));

        Assert.Empty(inbox.EnqueuedFrames);
        Assert.NotNull(context.LastCommandMessage);
    }

    [Fact]
    public void PlayerCommandHandler_EnqueuesCommandForJoinedSession()
    {
        RecordingPlayerCommandInbox inbox = new();
        ServerRoutingContext context = new("connection-1", commandInbox: inbox)
        {
            LastJoinRequest = new ClientJoinRequestMessage("session-a", "player-a")
        };
        PlayerCommandHandler handler = new();

        handler.Handle(context, new PlayerCommandMessage(CreateCommandFrame("session-a", 3)));

        PlayerCommandFrame frame = Assert.Single(inbox.EnqueuedFrames);
        Assert.Equal("session-a", frame.SessionId.Value);
        Assert.Equal((ulong)3, frame.Sequence);
    }

    [Fact]
    public void JoinRequestHandler_WhenAuthorityAcceptsJoin_ReturnsJoinAcceptedMessage()
    {
        FakeAuthoritativeSession authority = new();
        ServerRoutingContext context = new("connection-1", authority);
        JoinRequestHandler handler = new();

        handler.Handle(context, new ClientJoinRequestMessage("session-a", "player-a"));

        JoinAcceptedMessage accepted = Assert.IsType<JoinAcceptedMessage>(Assert.Single(context.OutboundMessages));
        Assert.Equal("session-a", accepted.SessionId);
        Assert.Equal(101, accepted.ActorId);
        Assert.Equal((ulong)12, accepted.CurrentServerTick);
        PlayerRosterEntry rosterEntry = Assert.Single(accepted.EffectiveRoster);
        Assert.Equal("player-a", rosterEntry.DisplayName);
    }

    [Fact]
    public void PlayerActionHandler_WhenAuthorityIsAttachedAndSessionJoined_EnqueuesActionForTickProcessing()
    {
        FakeAuthoritativeSession authority = new();
        RecordingPlayerActionQueue actionQueue = new();
        ServerRoutingContext context = new("connection-1", authority, actionQueue: actionQueue)
        {
            LastJoinRequest = new ClientJoinRequestMessage("session-a", "player-a")
        };
        PlayerActionHandler handler = new();

        // Build and combat use are not client-sendable over player.action (NETR-6); an equip is.
        handler.Handle(context, new PlayerActionMessage(
            new PlayerSessionId("session-a"),
            9,
            new InventoryEquipIntent(2)));

        Assert.Empty(context.OutboundMessages);
        QueuedPlayerAction queuedAction = Assert.Single(actionQueue.EnqueuedActions);
        Assert.Equal("session-a", queuedAction.SessionId.Value);
        Assert.Equal((ulong)9, queuedAction.Message.ActionSequence);
    }

    private static PlayerCommandFrame CreateCommandFrame(string sessionId, ulong sequence)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId(sessionId),
            sequence,
            PlayerModeSwitchIntent.None,
            PlayerMoveIntent.None,
            PlayerLookIntent.None,
            PrimaryUsePressed: false);
    }

    private static DedicatedServerHost CreateHost(IAuthoritativeSessionHost authority)
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

    private sealed class RecordingPlayerCommandInbox : IPlayerCommandInbox
    {
        public List<PlayerCommandFrame> EnqueuedFrames { get; } = [];

        public void Enqueue(PlayerCommandFrame commandFrame)
        {
            EnqueuedFrames.Add(commandFrame);
        }

        public IReadOnlyList<PlayerCommandFrame> Drain(IEnumerable<PlayerSessionId> sessionIds)
        {
            return [];
        }
    }

    private sealed class RecordingPlayerActionQueue : IPlayerActionQueue
    {
        public List<QueuedPlayerAction> EnqueuedActions { get; } = [];

        public QueuedPlayerAction Enqueue(PlayerActionMessage message)
        {
            QueuedPlayerAction queuedAction = new(
                (ulong)(EnqueuedActions.Count + 1),
                message.SessionId,
                message);
            EnqueuedActions.Add(queuedAction);
            return queuedAction;
        }

        public IReadOnlyList<QueuedPlayerAction> Drain(IEnumerable<PlayerSessionId> joinedSessionIds)
        {
            return [];
        }
    }

    private sealed class FakeAuthoritativeSession : IAuthoritativeSessionHost
    {
        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
        {
            return new JoinAcceptedMessage(
                requestedSessionId,
                101,
                12,
                InitialWorld: null,
                [
                    new PlayerRosterEntry(requestedSessionId, 101, displayName)
                ]);
        }

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message)
        {
            return new AuthoritativeActionResult(
                message.SessionId.Value,
                message.ActionSequence,
                12,
                Accepted: true,
                Payload: new WorldMutationResult(WorldMutationKind.CubePlaced, new OpenTK.Mathematics.Vector3(4f, 1f, 4f), true));
        }

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false)
        {
            return new ServerSnapshotEnvelope(
                12,
                new Dictionary<string, ulong>(),
                Array.Empty<ActorSnapshot>(),
                PlayerInteractionMode.Building,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>());
        }
    }
}
