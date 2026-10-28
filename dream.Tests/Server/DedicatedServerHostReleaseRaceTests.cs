using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Server;

// Wave 8 fix loop: a released connection must not leave state behind that a reconnect of the
// same session (or a later server-side request) trips over.
public class DedicatedServerHostReleaseRaceTests
{
    private const string Session = "session-a";
    private static readonly PlayerSessionId SessionId = new(Session);

    // HandleClientMessage fetches the connection's context before it takes the tick lock. A command
    // that slipped through that window after the release enqueued a frame (sequence 50) under the
    // released session; without a fresh ring per claim, the reconnect's commands from sequence 1
    // were dropped as stale behind it.
    [Fact]
    public void StaleCommandFromAReleasedContext_DoesNotBlockARejoinFromSequenceOne()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage("connection-old", new ClientJoinRequestMessage(Session, "A"))));
        host.HandleClientMessage("connection-old", new PlayerCommandMessage(Command(49)));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        Assert.True(host.TryGetConnectionContext("connection-old", out ServerRoutingContext? oldContext));

        host.ReleaseConnection("connection-old");
        // The old context's late command lands after the release (what the unlocked window allowed).
        oldContext!.CommandInbox!.Enqueue(Command(50));

        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage("connection-new", new ClientJoinRequestMessage(Session, "A"))));
        for (ulong sequence = 1; sequence <= 3; sequence++)
        {
            host.HandleClientMessage("connection-new", new PlayerCommandMessage(Command(sequence)));
        }

        for (ulong expected = 1; expected <= 3; expected++)
        {
            host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
            Assert.Equal(expected, AcceptedSequence(host));
        }
    }

    // The stale message itself, routed after the release: it neither reaches the inbox nor brings
    // the released connection back as a joined context.
    [Fact]
    public void MessageForAReleasedConnection_IsDropped_AndDoesNotRejoinIt()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();
        host.HandleClientMessage("connection-old", new ClientJoinRequestMessage(Session, "A"));
        host.ReleaseConnection("connection-old");

        Assert.Empty(host.HandleClientMessage("connection-old", new PlayerCommandMessage(Command(60))));
        Assert.False(host.IsConnectionJoined("connection-old"));

        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage("connection-new", new ClientJoinRequestMessage(Session, "A"))));
        host.HandleClientMessage("connection-new", new PlayerCommandMessage(Command(1)));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        Assert.Equal(1UL, AcceptedSequence(host));
    }

    // The service's overflow resync for a connection the host already released: no ghost context.
    [Fact]
    public void ServerResync_ForAReleasedConnection_CreatesNoContext()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();
        host.HandleClientMessage("connection-old", new ClientJoinRequestMessage(Session, "A"));
        host.ReleaseConnection("connection-old");

        Assert.False(host.RequestWorldResyncIfConnected("connection-old"));
        Assert.Equal(0, host.ConnectionCount);
        Assert.False(host.TryGetConnectionContext("connection-old", out _));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        Assert.Empty(replies);
    }

    // True while a resync is pending; false once the cooldown refuses one, so the caller knows to
    // ask again later.
    [Fact]
    public void ServerResync_ReportsWhetherTheCooldownAcceptedIt()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();
        Assert.False(host.RequestWorldResyncIfConnected("connection-a"));
        host.HandleClientMessage("connection-a", new ClientJoinRequestMessage(Session, "A"));

        Assert.True(host.RequestWorldResyncIfConnected("connection-a"));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        Assert.IsType<ServerSnapshotMessage>(Assert.Single(replies).Message);

        // Served; a second request inside the cooldown is refused.
        Assert.False(host.RequestWorldResyncIfConnected("connection-a"));
    }

    private static ulong? AcceptedSequence(DedicatedServerHost host)
    {
        Assert.True(host.Runtime!.Sessions.TryGet(SessionId, out ConnectedPlayer? player));
        return player!.LastAcceptedCommandSequence;
    }

    private static PlayerCommandFrame Command(ulong sequence)
    {
        return new PlayerCommandFrame(
            SessionId,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(1f, 0f),
            PlayerLookIntent.None,
            PrimaryUsePressed: false);
    }
}
