using System.Text;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Characters;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.BugHunt;

// BH-NET-1: a join request whose requestedSessionId is blank (or whose fields are missing outright)
// used to throw ArgumentException out of RuntimeAuthoritativeSessionHost.JoinSession, past
// JoinRequestHandler's InvalidOperationException-only catch, and — on a host that defers joins to the
// tick, which is the production configuration — out of ApplyPendingJoins and the tick loop. One frame
// from any connected client stopped the dedicated server simulating. The malformed field is now refused
// at the protocol boundary and in the handler, for that one connection.
public sealed class NetJoinRequestValidationTests
{
    private static readonly TransportJsonCodec Codec = new();

    private const string ValidSession = "session-ok";

    // --- the parse boundary -------------------------------------------------------------------

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    public void BlankSessionId_IsRefusedWhenTheFrameIsDecodedFromAClient(string sessionIdJson)
    {
        string json = "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion +
            ",\"messageKind\":\"client.join-request\",\"sequence\":1,\"payload\":" +
            "{\"requestedSessionId\":" + sessionIdJson + ",\"displayName\":\"Bob\"}}";

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Codec.DecodeFromClient(Encoding.UTF8.GetBytes(json)));

        Assert.Contains("requestedSessionId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSessionIdProperty_IsRefusedWhenTheFrameIsDecodedFromAClient()
    {
        string json = "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion +
            ",\"messageKind\":\"client.join-request\",\"sequence\":1,\"payload\":{\"displayName\":\"Bob\"}}";

        Assert.Throws<InvalidOperationException>(() => Codec.DecodeFromClient(Encoding.UTF8.GetBytes(json)));
    }

    // Sibling defect found in the same parse path: displayName is a non-nullable record parameter that
    // System.Text.Json still binds to null when the property is missing, and the server-full seat check
    // calls .Trim() on it. It now decodes as empty, which is the value a blank name already had.
    [Fact]
    public void MissingDisplayName_DecodesAsEmpty_NotNull()
    {
        string json = "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion +
            ",\"messageKind\":\"client.join-request\",\"sequence\":1,\"payload\":" +
            "{\"requestedSessionId\":\"" + ValidSession + "\"}}";

        ClientJoinRequestMessage decoded = Assert.IsType<ClientJoinRequestMessage>(
            Codec.DecodeFromClient(Encoding.UTF8.GetBytes(json)).Message);

        Assert.Equal(string.Empty, decoded.DisplayName);
        Assert.Equal(ValidSession, decoded.RequestedSessionId);
    }

    [Fact]
    public void AWellFormedJoinRequest_StillDecodes()
    {
        string json = "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion +
            ",\"messageKind\":\"client.join-request\",\"sequence\":1,\"payload\":" +
            "{\"requestedSessionId\":\"" + ValidSession + "\",\"displayName\":\"Bob\"}}";

        ClientJoinRequestMessage decoded = Assert.IsType<ClientJoinRequestMessage>(
            Codec.DecodeFromClient(Encoding.UTF8.GetBytes(json)).Message);

        Assert.Equal(ValidSession, decoded.RequestedSessionId);
        Assert.Equal("Bob", decoded.DisplayName);
    }

    // --- the handler --------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TheHandler_RejectsABlankSessionId_WithoutThrowing_AndClaimsNothing(string sessionId)
    {
        using RuntimeAuthoritativeSessionHost authority = RuntimeAuthoritativeSessionHost.CreateDefault();
        RecordingClaims claims = new();
        ServerRoutingContext context = new("connection-blank", authority, sessionClaims: claims);

        new JoinRequestHandler().Handle(context, new ClientJoinRequestMessage(sessionId, "Bob"));

        JoinRejectedMessage rejected = Assert.IsType<JoinRejectedMessage>(Assert.Single(context.OutboundMessages));
        Assert.Equal(JoinRequestHandler.SessionIdInvalid, rejected.Reason);
        Assert.Empty(claims.Claimed);
        Assert.Null(context.LastJoinRequest);
    }

    [Fact]
    public void TheHandler_StillAcceptsAWellFormedJoin()
    {
        using RuntimeAuthoritativeSessionHost authority = RuntimeAuthoritativeSessionHost.CreateDefault();
        RecordingClaims claims = new();
        ServerRoutingContext context = new("connection-ok", authority, sessionClaims: claims);

        new JoinRequestHandler().Handle(context, new ClientJoinRequestMessage(ValidSession, "Bob"));

        Assert.IsType<JoinAcceptedMessage>(Assert.Single(context.OutboundMessages));
        Assert.Equal([ValidSession], claims.Claimed);
    }

    // An authority that throws ArgumentException for this connection's own field must be answered with a
    // rejection and must release the claim, exactly as a refusal (InvalidOperationException) is.
    [Fact]
    public void TheHandler_TurnsAnArgumentFailureIntoARejection_AndReleasesTheClaim()
    {
        RecordingClaims claims = new();
        ServerRoutingContext context = new("connection-throwing", new ThrowingAuthority(), sessionClaims: claims);

        new JoinRequestHandler().Handle(context, new ClientJoinRequestMessage(ValidSession, "Bob"));

        Assert.IsType<JoinRejectedMessage>(Assert.Single(context.OutboundMessages));
        Assert.Equal(["connection-throwing"], claims.Released);
    }

    // --- the tick -----------------------------------------------------------------------------

    [Fact]
    public void ABlankSessionIdJoin_IsRejected_AndTheServerKeepsTicking()
    {
        using DedicatedServerHost host = CreateDeferredJoinHost();

        Assert.Empty(host.HandleClientMessage("connection-blank", new ClientJoinRequestMessage(string.Empty, "Bob")));
        AdvanceOneTick(host);

        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        PendingConnectionReply reply = Assert.Single(replies);
        Assert.Equal("connection-blank", reply.ConnectionId);
        Assert.IsType<JoinRejectedMessage>(reply.Message);

        // The loop is still alive and still joins real players.
        Assert.Empty(host.HandleClientMessage("connection-ok", new ClientJoinRequestMessage(ValidSession, "Ada")));
        AdvanceOneTick(host);
        host.DrainPendingJoinRepliesInto(replies);
        Assert.Contains(replies, r => r.ConnectionId == "connection-ok" && r.Message is JoinAcceptedMessage);
        AdvanceOneTick(host);
    }

    private static DedicatedServerHost CreateDeferredJoinHost()
    {
        WorldSessionRuntime runtime = new(12, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(new RuntimeAuthoritativeSessionHost(runtime), router, deferJoinsToTick: true);
    }

    private static void AdvanceOneTick(DedicatedServerHost host)
    {
        int executed = host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        if (executed == 0)
        {
            executed = host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
        }

        Assert.Equal(1, executed);
    }

    private sealed class RecordingClaims : IServerSessionClaimRegistry
    {
        public List<string> Claimed { get; } = [];

        public List<string> Released { get; } = [];

        public bool TryClaimSession(string connectionId, string sessionId, string? displayName, out string? rejectionReason)
        {
            Claimed.Add(sessionId);
            rejectionReason = null;
            return true;
        }

        public void ReleaseConnection(string connectionId) => Released.Add(connectionId);
    }

    // Stands in for an authority whose API refuses a client-supplied field with ArgumentException.
    private sealed class ThrowingAuthority : IAuthoritativeSessionHost
    {
        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName) =>
            throw new ArgumentException("Bad join field.", nameof(requestedSessionId));

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message) => throw new NotSupportedException();

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false) => throw new NotSupportedException();
    }
}
