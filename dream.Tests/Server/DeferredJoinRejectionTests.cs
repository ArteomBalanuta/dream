using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Server;

// A deferred join the authority refuses releases its connection while the host is applying the
// tick's joins. That must not drop or skip the other joins applied in the same tick.
public class DeferredJoinRejectionTests
{
    [Fact]
    public void RejectedDeferredJoin_DoesNotSkipTheNextJoinInTheSameTick()
    {
        RefusingAuthority authority = new("session-b");
        using DedicatedServerHost host = CreateHost(authority);
        host.HandleClientMessage("connection-3", new ClientJoinRequestMessage("session-c", "c"));
        host.HandleClientMessage("connection-2", new ClientJoinRequestMessage("session-b", "b"));
        host.HandleClientMessage("connection-1", new ClientJoinRequestMessage("session-a", "a"));

        Assert.Equal(1, host.AdvanceTime(SimulationClock.FixedDeltaSeconds));

        Assert.Equal(["session-a", "session-b", "session-c"], authority.Attempts);
        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        Assert.Equal(
            ["connection-1:server.join-accepted", "connection-2:server.join-rejected", "connection-3:server.join-accepted"],
            replies.Select(reply => $"{reply.ConnectionId}:{reply.Message.MessageKind}"));
        Assert.True(host.IsConnectionJoined("connection-1"));
        Assert.False(host.IsConnectionJoined("connection-2"));
        Assert.True(host.IsConnectionJoined("connection-3"));
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
        return new DedicatedServerHost(authority, router, deferJoinsToTick: true);
    }

    private sealed class RefusingAuthority(string refusedSessionId) : ITickDrivenAuthoritativeSessionHost
    {
        public List<string> Attempts { get; } = [];

        public ulong CurrentServerTick { get; private set; }

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
        {
            Attempts.Add(requestedSessionId);
            if (requestedSessionId == refusedSessionId)
            {
                throw new InvalidOperationException($"{requestedSessionId} is refused.");
            }

            return new JoinAcceptedMessage(requestedSessionId, 100 + Attempts.Count, CurrentServerTick, InitialWorld: null);
        }

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message)
        {
            throw new InvalidOperationException("Actions are queued for the tick.");
        }

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false)
        {
            return new ServerSnapshotEnvelope(
                CurrentServerTick,
                new Dictionary<string, ulong>(),
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
            return Array.Empty<AuthoritativeActionResult>();
        }
    }
}
