using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Server;

// S3: releasing a connection used to drop its session claim before purging the session's queued
// actions and releasing its runtime seat, outside the tick lock. A reconnect that reused the
// session id could claim it in that gap, queue an action and then lose it to the purge, and the
// late runtime release could detach its new seat. The release now runs under the tick lock and
// drops the claim last. The test hook runs inside the release, after the claim is confirmed and
// before the purge, and starts the reconnect there, which reproduces the old interleaving.
//
// The reconnect runs on a dedicated thread (a Task could be inlined by Wait onto the releasing
// thread, which holds the lock re-entrantly), and the thread is always joined before the host is
// disposed, so a failed assertion never leaves it touching a disposed runtime.
public class SessionReleaseReconnectRaceTests
{
    private const string Session = "session-a";
    private const string OldConnection = "connection-old";
    private const string NewConnection = "connection-new";
    private static readonly TimeSpan BlockedProbe = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Completion = TimeSpan.FromSeconds(30);

    [Fact]
    public void ReconnectDuringRelease_WaitsForTheRelease_AndKeepsItsActionAndSeat()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage(Session, "A"))));

        IReadOnlyList<IServerToClientMessage>? joinReplies = null;
        IReadOnlyList<IServerToClientMessage>? actionReplies = null;
        Exception? reconnectError = null;
        Thread? reconnect = null;
        bool reconnectFinishedInsideRelease = true;
        string? releasedSession = null;
        host.ReleasingSessionForTest = sessionId =>
        {
            releasedSession = sessionId;
            reconnect = StartThread(() =>
            {
                try
                {
                    joinReplies = host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage(Session, "A"));
                    actionReplies = host.HandleClientMessage(
                        NewConnection,
                        new PlayerActionMessage(new PlayerSessionId(Session), 1, new InventoryEquipIntent(0)));
                }
                catch (Exception ex)
                {
                    reconnectError = ex;
                }
            });

            // The reconnect needs the tick lock this release holds, so it cannot finish here.
            reconnectFinishedInsideRelease = reconnect.Join(BlockedProbe);
        };

        try
        {
            host.ReleaseConnection(OldConnection);
        }
        finally
        {
            host.ReleasingSessionForTest = null;
            Assert.NotNull(reconnect);
            Assert.True(reconnect!.Join(Completion), "the reconnect never finished");
        }

        Assert.Null(reconnectError);
        Assert.Equal(Session, releasedSession);
        Assert.False(reconnectFinishedInsideRelease, "the reconnect ran while the release was still in progress");
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(joinReplies!));
        Assert.Empty(actionReplies!);

        // The released session's runtime seat was detached before the reconnect re-attached it.
        Assert.True(host.Runtime!.Sessions.TryGet(new PlayerSessionId(Session), out ConnectedPlayer? player));
        Assert.True(player!.IsSpawned);

        // The action queued by the reconnect survived the purge and runs in the next tick.
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        AuthoritativeActionResult result = Assert.Single(host.DrainPendingActionResults());
        Assert.Equal(Session, result.SessionId);
        Assert.Equal((ulong)1, result.ActionSequence);
        Assert.True(host.Runtime.Sessions.TryGet(new PlayerSessionId(Session), out player));
        Assert.True(player!.IsSpawned);
    }

    [Fact]
    public void ReconnectDuringRelease_WithDeferredJoins_IsSeatedAtTheNextTick()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: true);
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage(Session, "A"));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        Assert.True(host.IsConnectionJoined(OldConnection));
        DrainReplies(host);

        Exception? reconnectError = null;
        Thread? reconnect = null;
        bool reconnectFinishedInsideRelease = true;
        host.ReleasingSessionForTest = _ =>
        {
            reconnect = StartThread(() =>
            {
                try
                {
                    host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage(Session, "A"));
                }
                catch (Exception ex)
                {
                    reconnectError = ex;
                }
            });
            reconnectFinishedInsideRelease = reconnect.Join(BlockedProbe);
        };

        try
        {
            host.ReleaseConnection(OldConnection);
        }
        finally
        {
            host.ReleasingSessionForTest = null;
            Assert.NotNull(reconnect);
            Assert.True(reconnect!.Join(Completion), "the reconnect never finished");
        }

        Assert.Null(reconnectError);
        Assert.False(reconnectFinishedInsideRelease, "the deferred join was queued while the release was still in progress");
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);

        PendingConnectionReply reply = Assert.Single(DrainReplies(host));
        Assert.Equal(NewConnection, reply.ConnectionId);
        Assert.IsType<JoinAcceptedMessage>(reply.Message);
        Assert.True(host.IsConnectionJoined(NewConnection));
        Assert.True(host.Runtime!.Sessions.TryGet(new PlayerSessionId(Session), out ConnectedPlayer? player));
        Assert.True(player!.IsSpawned);
    }

    [Fact]
    public void Release_FreesTheClaim_SoTheSessionCanBeClaimedAgain()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage(Session, "A"));

        Assert.False(host.TryClaimSession(NewConnection, Session, out string? reason));
        Assert.NotNull(reason);

        host.ReleaseConnection(OldConnection);

        Assert.True(host.TryClaimSession(NewConnection, Session, out reason));
        Assert.Null(reason);
        Assert.False(host.TryGetConnectionContext(OldConnection, out _));
    }

    private static Thread StartThread(Action body)
    {
        Thread thread = new(() => body()) { IsBackground = true, Name = "reconnect-race" };
        thread.Start();
        return thread;
    }

    private static List<PendingConnectionReply> DrainReplies(DedicatedServerHost host)
    {
        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        return replies;
    }
}
