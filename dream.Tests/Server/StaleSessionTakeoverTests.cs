using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Server;

// NETR-8: a client that crashed (or lost its network) and rejoins used to be rejected until the
// server's 10 s idle timeout released its old connection. A join now takes over a session, or a
// player name, whose holder has sent nothing for DedicatedServerHost.StaleSessionTakeoverSilence;
// a holder heard from inside that window keeps it and the join is rejected. Every clock here is
// a ManualTimeProvider, so silence is exact.
public sealed class StaleSessionTakeoverTests
{
    private const string PlayerName = "Player A";
    private const string OldConnection = "connection-old";
    private const string NewConnection = "connection-new";
    private static readonly TimeSpan PastTakeoverWindow = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan InsideTakeoverWindow = TimeSpan.FromSeconds(1.5);

    [Fact]
    public void TakeoverWindow_IsTwoSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), DedicatedServerHost.StaleSessionTakeoverSilence);
        Assert.Contains("Session already in use", DedicatedServerHost.SessionInUseRejectionReason);
    }

    [Fact]
    public void CrashRejoin_SameSessionId_AfterTwoSecondsOfSilence_TakesOverTheSeatAndProfile()
    {
        using PersistenceTestDatabase database = new();
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreatePersistentHost(database, clock);
        WorldSessionRuntime runtime = host.Runtime!;
        JoinAndTick(host, OldConnection, "session-a", PlayerName);
        Assert.Equal("session-a", runtime.PrimaryRemoteSessionId?.Value);
        runtime.Player.Combatant.ApplyProgression(4, 25);

        // The client crashed 2.5 s ago and comes back, well inside the 10 s idle timeout.
        clock.Advance(PastTakeoverWindow);
        PendingConnectionReply reply = Assert.Single(JoinAndTick(host, NewConnection, "session-a", PlayerName));

        Assert.Equal(NewConnection, reply.ConnectionId);
        Assert.IsType<JoinAcceptedMessage>(reply.Message);
        Assert.True(host.IsConnectionJoined(NewConnection));
        Assert.False(host.TryGetConnectionContext(OldConnection, out _));
        Assert.Equal("session-a", runtime.PrimaryRemoteSessionId?.Value);
        Assert.Equal(4, runtime.Player.Combatant.Level);
        Assert.Equal(25, runtime.Player.Combatant.Experience);
        Assert.True(runtime.Sessions.TryGet(new PlayerSessionId("session-a"), out ConnectedPlayer? player));
        Assert.True(player!.IsSpawned);

        // The service is told to close the old connection.
        Assert.True(host.TryTakeTakenOverConnection(out string closed));
        Assert.Equal(OldConnection, closed);
        Assert.False(host.TryTakeTakenOverConnection(out _));
    }

    [Fact]
    public void CrashRejoin_NewSessionIdSameDisplayName_TakesOverThePlayersSeatAndProfile()
    {
        // A restarted client picks a fresh "client-<guid>" session id; its persisted identity is
        // the display name.
        using PersistenceTestDatabase database = new();
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreatePersistentHost(database, clock);
        WorldSessionRuntime runtime = host.Runtime!;
        JoinAndTick(host, OldConnection, "client-old", PlayerName);
        runtime.Player.Combatant.ApplyProgression(5, 30);

        clock.Advance(PastTakeoverWindow);
        PendingConnectionReply reply = Assert.Single(JoinAndTick(host, NewConnection, "client-new", PlayerName));

        Assert.IsType<JoinAcceptedMessage>(reply.Message);
        Assert.Equal("client-new", runtime.PrimaryRemoteSessionId?.Value);
        Assert.Equal(5, runtime.Player.Combatant.Level);
        Assert.Equal(30, runtime.Player.Combatant.Experience);
        Assert.True(runtime.Sessions.TryGet(new PlayerSessionId("client-old"), out ConnectedPlayer? old));
        Assert.False(old!.IsSpawned);
        Assert.False(host.IsConnectionJoined(OldConnection));
        Assert.True(host.TryTakeTakenOverConnection(out string closed));
        Assert.Equal(OldConnection, closed);
    }

    [Fact]
    public void Rejoin_WhileTheOldConnectionIsLive_IsRejectedAsSessionInUse()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage("session-a", PlayerName))));

        clock.Advance(InsideTakeoverWindow);
        JoinRejectedMessage rejected = Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("session-a", PlayerName))));

        Assert.Equal(DedicatedServerHost.SessionInUseRejectionReason, rejected.Reason);
        Assert.True(host.IsConnectionJoined(OldConnection));
        Assert.False(host.IsConnectionJoined(NewConnection));
        Assert.False(host.TryTakeTakenOverConnection(out _));
    }

    [Fact]
    public void KeepaliveFrames_KeepTheOldConnectionLive()
    {
        // Empty keepalive frames never reach the router; the service reports the transport's frame
        // count instead, and a count that moved is inbound traffic.
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage("session-a", PlayerName));
        host.ObserveInboundFrames(OldConnection, 1);

        clock.Advance(TimeSpan.FromSeconds(1.9));
        host.ObserveInboundFrames(OldConnection, 2);
        clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("session-a", PlayerName))));

        // An unchanged count is no traffic: the old connection goes stale.
        host.ObserveInboundFrames(OldConnection, 2);
        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("session-a", PlayerName))));
    }

    [Fact]
    public void SameDisplayName_WhileTheOldConnectionIsLive_IsRejected_AndTheOldPlayerKeepsItsSeat()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage("client-old", PlayerName));

        clock.Advance(InsideTakeoverWindow);
        Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("client-new", PlayerName))));

        Assert.True(host.IsConnectionJoined(OldConnection));
        Assert.Equal("client-old", host.Runtime!.PrimaryRemoteSessionId?.Value);
        Assert.False(host.TryTakeTakenOverConnection(out _));
    }

    [Fact]
    public void StaleHolderOfAnotherName_IsNotTakenOver()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage("client-old", "Somebody Else"));

        clock.Advance(PastTakeoverWindow);
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("client-new", PlayerName))));

        Assert.True(host.IsConnectionJoined(OldConnection));
        Assert.False(host.TryTakeTakenOverConnection(out _));
    }

    [Fact]
    public void LateMessagesAndReleaseFromTheOldConnection_LeaveNoGhost_AndKeepTheNewConnectionsActions()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        PlayerSessionId session = new("session-a");
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage(session.Value, PlayerName));

        clock.Advance(PastTakeoverWindow);
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage(session.Value, PlayerName))));
        Assert.Empty(host.HandleClientMessage(NewConnection, new PlayerActionMessage(session, 1, new InventoryEquipIntent(0))));

        // Frames the old connection had in flight, then its own loop's release.
        Assert.Empty(host.HandleClientMessage(OldConnection, new PlayerActionMessage(session, 7, new InventoryEquipIntent(0))));
        Assert.Empty(host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage(session.Value, PlayerName)));
        Assert.False(host.TryGetConnectionContext(OldConnection, out _));
        host.ReleaseConnection(OldConnection);
        // A frame still in flight when the release ran is dropped too.
        Assert.Empty(host.HandleClientMessage(OldConnection, new PlayerActionMessage(session, 8, new InventoryEquipIntent(0))));
        Assert.False(host.TryGetConnectionContext(OldConnection, out _));

        Assert.Equal(1, host.ConnectionCount);
        Assert.True(host.IsConnectionJoined(NewConnection));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        AuthoritativeActionResult result = Assert.Single(host.DrainPendingActionResults());
        Assert.Equal(session.Value, result.SessionId);
        Assert.Equal((ulong)1, result.ActionSequence);
        Assert.True(host.Runtime!.Sessions.TryGet(session, out ConnectedPlayer? player));
        Assert.True(player!.IsSpawned);
    }

    [Fact]
    public void OldConnectionsReleaseRacingTheTakeover_WaitsForIt_AndLeavesTheNewSeat()
    {
        // The old connection's loop ends (or its idle fault fires) while the takeover is releasing
        // it. Both run under the tick lock: the late release waits and then finds nothing to do.
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        PlayerSessionId session = new("session-a");
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage(session.Value, PlayerName));
        clock.Advance(PastTakeoverWindow);

        Thread? racer = null;
        Exception? racerError = null;
        bool racerFinishedInsideTakeover = true;
        host.ReleasingSessionForTest = _ =>
        {
            racer = new Thread(() =>
            {
                try
                {
                    host.ReleaseConnection(OldConnection);
                    host.HandleClientMessage(OldConnection, new PlayerActionMessage(session, 9, new InventoryEquipIntent(0)));
                }
                catch (Exception ex)
                {
                    racerError = ex;
                }
            })
            {
                IsBackground = true
            };
            racer.Start();
            racerFinishedInsideTakeover = racer.Join(TimeSpan.FromMilliseconds(200));
        };

        IReadOnlyList<IServerToClientMessage> joinReplies;
        try
        {
            joinReplies = host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage(session.Value, PlayerName));
        }
        finally
        {
            host.ReleasingSessionForTest = null;
            Assert.NotNull(racer);
            Assert.True(racer!.Join(TimeSpan.FromSeconds(30)), "the racing release never finished");
        }

        Assert.Null(racerError);
        Assert.False(racerFinishedInsideTakeover, "the racing release ran while the takeover held the lock");
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(joinReplies));
        Assert.Empty(host.HandleClientMessage(NewConnection, new PlayerActionMessage(session, 1, new InventoryEquipIntent(0))));
        Assert.False(host.TryGetConnectionContext(OldConnection, out _));

        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        AuthoritativeActionResult result = Assert.Single(host.DrainPendingActionResults());
        Assert.Equal((ulong)1, result.ActionSequence);
        Assert.True(host.IsConnectionJoined(NewConnection));
        Assert.True(host.Runtime!.Sessions.TryGet(session, out ConnectedPlayer? player));
        Assert.True(player!.IsSpawned);
    }

    [Fact]
    public async Task Service_ClosesTheTakenOverConnection()
    {
        ManualTimeProvider clock = new();
        ServerConnectionOptions options = new()
        {
            IdleTimeout = TimeSpan.FromSeconds(10),
            SendStallTimeout = TimeSpan.FromSeconds(5),
            TimeProvider = clock
        };
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: true);
        DedicatedServerService service = new(host, new NoopServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        GatedTransport oldTransport = new(OldConnection, recordFrames: false);
        GatedTransport newTransport = new(NewConnection, recordFrames: false);
        service.AttachConnection(new ServerConnection(oldTransport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options));
        service.AttachConnection(new ServerConnection(newTransport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options));
        try
        {
            host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage("session-a", PlayerName));
            await AdvanceUntilAsync(service, () => host.IsConnectionJoined(OldConnection));

            clock.Advance(PastTakeoverWindow);
            host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("session-a", PlayerName));
            await AdvanceUntilAsync(service, () => service.ConnectionCount == 1);

            Assert.True(oldTransport.IsDisposed);
            Assert.False(newTransport.IsDisposed);
            Assert.True(host.IsConnectionJoined(NewConnection));
            Assert.False(host.TryGetConnectionContext(OldConnection, out _));
            Assert.Equal(1, host.ConnectionCount);
            Assert.Equal("session-a", host.Runtime!.PrimaryRemoteSessionId?.Value);
        }
        finally
        {
            await service.DisposeAsync();
            host.Dispose();
        }
    }

    private static async Task AdvanceUntilAsync(DedicatedServerService service, Func<bool> condition)
    {
        for (int i = 0; i < 64 && !condition(); i++)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        }

        Assert.True(condition());
    }

    private static DedicatedServerHost CreateHost(ManualTimeProvider clock)
    {
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();
        host.TimeProvider = clock;
        return host;
    }

    private static DedicatedServerHost CreatePersistentHost(PersistenceTestDatabase database, ManualTimeProvider clock)
    {
        WorldSessionRuntime runtime = new(
            12,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            persistenceWrites: PersistenceWriteQueue.Background());
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(new RuntimeAuthoritativeSessionHost(runtime), router, deferJoinsToTick: true)
        {
            TimeProvider = clock
        };
    }

    // Queues a deferred join, waits for its prefetch and ticks until a reply for it arrives.
    private static List<PendingConnectionReply> JoinAndTick(DedicatedServerHost host, string connectionId, string sessionId, string displayName)
    {
        Assert.Empty(host.HandleClientMessage(connectionId, new ClientJoinRequestMessage(sessionId, displayName)));
        List<PendingConnectionReply> replies = [];
        List<PendingConnectionReply> drained = [];
        for (int i = 0; i < 64 && replies.Count == 0; i++)
        {
            Assert.True(host.Runtime!.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)), "The persistence worker did not drain.");
            int executed = host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
            if (executed == 0)
            {
                host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
            }

            host.DrainPendingJoinRepliesInto(drained);
            replies.AddRange(drained);
        }

        Assert.NotEmpty(replies);
        return replies;
    }
}
