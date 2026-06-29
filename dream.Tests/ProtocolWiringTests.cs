using Xunit;

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
        DedicatedServerHost host = DedicatedServerHost.CreateDefault();

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
    public void PlayerActionHandler_WhenAuthorityIsAttachedAndSessionJoined_ReturnsAcceptedServerActionMessage()
    {
        FakeAuthoritativeSession authority = new();
        ServerRoutingContext context = new("connection-1", authority)
        {
            LastJoinRequest = new ClientJoinRequestMessage("session-a", "player-a")
        };
        PlayerActionHandler handler = new();

        handler.Handle(context, new PlayerActionMessage(
            new PlayerSessionId("session-a"),
            9,
            new BuildUseIntent(new OpenTK.Mathematics.Vector3(4f, 1f, 4f))));

        ServerActionMessage reply = Assert.IsType<ServerActionMessage>(Assert.Single(context.OutboundMessages));
        Assert.True(reply.Result.Accepted);
        Assert.NotNull(reply.Result.Payload);
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
