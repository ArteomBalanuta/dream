using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Server;

// NETR-8: a client that crashed (or lost its network) and rejoins used to be rejected until the
// server's 10 s idle timeout released its old connection. A join now takes over a session id or
// a player name whose holder has sent nothing for DedicatedServerHost.TakeoverSilence (8 s, one
// window for both: session ids are broadcast, so a shorter session-id window would let a
// hijacker skip the name window); a holder heard from inside the window keeps it and the join is
// rejected, and later joins for the contested session id or name from the same source address
// are throttled. Connection ids that are not "ip:port" endpoints are their own source. Every
// clock here is a ManualTimeProvider, so silence is exact.
public sealed class StaleSessionTakeoverTests
{
    private const string PlayerName = "Player A";
    private const string OldConnection = "connection-old";
    private const string NewConnection = "connection-new";
    private static readonly TimeSpan PastTakeoverWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan InsideTakeoverWindow = TimeSpan.FromSeconds(1.5);

    [Fact]
    public void TakeoverWindow_IsOneEightSecondWindow_WellPastKeepaliveGaps_AndUnderTheIdleTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), DedicatedServerHost.TakeoverSilence);
        Assert.True(DedicatedServerHost.TakeoverSilence >= 5 * RemoteClientSession.KeepaliveInterval);
        Assert.True(DedicatedServerHost.TakeoverSilence < ServerConnectionOptions.Default.IdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), ServerRoutingContext.JoinThrottleInitialInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), ServerRoutingContext.JoinThrottleMaxInterval);
        Assert.Contains("Session already in use", DedicatedServerHost.SessionInUseRejectionReason);
    }

    [Fact]
    public void Hijacker_WithTheVictimsSessionIdAndName_IsRejectedAtThreeAndSevenPointNineSeconds_AndAcceptedOnlyAtEight()
    {
        // Reviewer S1: session ids are broadcast in snapshots, and the desktop client never rejoins
        // under its old one, so a session-id match must wait as long as a name match.
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage("client-victim", PlayerName));

        clock.Advance(TimeSpan.FromSeconds(3));
        JoinRejectedMessage rejected = Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage("hijacker-1", new ClientJoinRequestMessage("client-victim", PlayerName))));
        Assert.Equal(DedicatedServerHost.SessionInUseRejectionReason, rejected.Reason);
        Assert.True(host.IsConnectionJoined(OldConnection));

        clock.Advance(TimeSpan.FromSeconds(4.9));
        rejected = Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage("hijacker-2", new ClientJoinRequestMessage("client-victim", PlayerName))));
        Assert.Equal(DedicatedServerHost.SessionInUseRejectionReason, rejected.Reason);
        Assert.True(host.IsConnectionJoined(OldConnection));
        Assert.False(host.TryTakeTakenOverConnection(out _));

        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage("hijacker-3", new ClientJoinRequestMessage("client-victim", PlayerName))));
        Assert.False(host.TryGetConnectionContext(OldConnection, out _));
        Assert.True(host.TryTakeTakenOverConnection(out string closed));
        Assert.Equal(OldConnection, closed);
    }

    [Fact]
    public void SameSessionId_IsTakenOverAtTheTakeoverWindow_NotJustUnderIt()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage("session-a", PlayerName));

        clock.Advance(DedicatedServerHost.TakeoverSilence - TimeSpan.FromSeconds(0.1));
        JoinRejectedMessage rejected = Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("session-a", "Someone Else"))));
        Assert.Equal(DedicatedServerHost.SessionInUseRejectionReason, rejected.Reason);
        Assert.True(host.IsConnectionJoined(OldConnection));

        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage("connection-third", new ClientJoinRequestMessage("session-a", "Someone Else"))));
        Assert.False(host.TryGetConnectionContext(OldConnection, out _));
        Assert.True(host.TryTakeTakenOverConnection(out string closed));
        Assert.Equal(OldConnection, closed);
    }

    [Fact]
    public void PlayerName_IsTakenOverAtEightSecondsOfSilence_NotAtFive()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage("client-old", PlayerName));

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("client-new", PlayerName))));
        Assert.True(host.IsConnectionJoined(OldConnection));
        Assert.False(host.TryTakeTakenOverConnection(out _));

        // Just inside the window: still the old player's.
        clock.Advance(TimeSpan.FromSeconds(2.9));
        Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage("connection-third", new ClientJoinRequestMessage("client-third", PlayerName))));
        Assert.True(host.IsConnectionJoined(OldConnection));

        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage("connection-fourth", new ClientJoinRequestMessage("client-fourth", PlayerName))));
        Assert.Equal("client-fourth", host.Runtime!.PrimaryRemoteSessionId?.Value);
        Assert.True(host.TryTakeTakenOverConnection(out string closed));
        Assert.Equal(OldConnection, closed);
    }

    [Fact]
    public void IdleClient_WithKeepaliveGapsUpToTwoSeconds_IsNeverTakenOver()
    {
        // A healthy idle client's frames are up to 1.5 s apart (keepalive after 1 s, checked every
        // 0.5 s); 0.5 s more of jitter stands for a retransmit or a Wi-Fi stall. The service sweeps
        // the frame count every tick (here every 1/32 s), and fresh connections keep trying both
        // the session id and the name for a minute.
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage("client-old", PlayerName));
        Random jitter = new(1234);
        long frames = 1;
        host.ObserveInboundFrames(OldConnection, frames);
        TimeSpan step = TimeSpan.FromSeconds(1d / 32);
        TimeSpan elapsed = TimeSpan.Zero;
        TimeSpan nextFrameAt = NextGap(jitter);
        TimeSpan nextAttemptAt = TimeSpan.FromSeconds(0.25);
        int attempts = 0;
        while (elapsed < TimeSpan.FromSeconds(60))
        {
            clock.Advance(step);
            elapsed += step;
            if (elapsed >= nextFrameAt)
            {
                frames++;
                nextFrameAt = elapsed + NextGap(jitter);
            }

            host.ObserveInboundFrames(OldConnection, frames);
            if (elapsed >= nextAttemptAt)
            {
                nextAttemptAt = elapsed + TimeSpan.FromSeconds(0.25);
                string sessionAttempt = $"session-attempt-{attempts}";
                string nameAttempt = $"name-attempt-{attempts}";
                attempts++;
                Assert.IsType<JoinRejectedMessage>(Assert.Single(
                    host.HandleClientMessage(sessionAttempt, new ClientJoinRequestMessage("client-old", "Someone"))));
                Assert.IsType<JoinRejectedMessage>(Assert.Single(
                    host.HandleClientMessage(nameAttempt, new ClientJoinRequestMessage($"client-{attempts}", PlayerName))));
                host.ReleaseConnection(sessionAttempt);
                host.ReleaseConnection(nameAttempt);
            }
        }

        Assert.True(attempts > 200);
        Assert.True(host.IsConnectionJoined(OldConnection));
        Assert.Equal("client-old", host.Runtime!.PrimaryRemoteSessionId?.Value);
        Assert.False(host.TryTakeTakenOverConnection(out _));

        static TimeSpan NextGap(Random random) => TimeSpan.FromSeconds(1.5 + random.NextDouble() * 0.5);
    }

    [Fact]
    public void JoinSpam_AgainstALiveHolder_IsThrottled_AndALegitimateRejoinStillWorksOnceItGoesStale()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage("client-old", PlayerName));
        long frames = 1;
        host.ObserveInboundFrames(OldConnection, frames);

        // Two spammers resend a join every 100 ms for 30 s: one with the holder's session id, one
        // with its name under another session id. The holder sends a keepalive every second.
        const string SessionSpammer = "spam-session";
        const string NameSpammer = "spam-name";
        int sessionProcessed = 0;
        int nameProcessed = 0;
        int attempts = 0;
        for (int i = 0; i <= 300; i++)
        {
            if (i > 0)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            if (i % 10 == 0)
            {
                frames++;
            }

            host.ObserveInboundFrames(OldConnection, frames);
            attempts++;
            IReadOnlyList<IServerToClientMessage> sessionReplies =
                host.HandleClientMessage(SessionSpammer, new ClientJoinRequestMessage("client-old", "Spammer"));
            IReadOnlyList<IServerToClientMessage> nameReplies =
                host.HandleClientMessage(NameSpammer, new ClientJoinRequestMessage("client-spam", PlayerName));
            if (sessionReplies.Count > 0)
            {
                Assert.IsType<JoinRejectedMessage>(Assert.Single(sessionReplies));
                sessionProcessed++;
            }

            if (nameReplies.Count > 0)
            {
                Assert.IsType<JoinRejectedMessage>(Assert.Single(nameReplies));
                nameProcessed++;
            }
        }

        // Processed at 0, 2, 6, 14 and 24 s (intervals 2, 4, 8, 10, 10 s); the rest dropped unanswered.
        Assert.Equal(5, sessionProcessed);
        Assert.Equal(5, nameProcessed);
        Assert.Equal(2L * attempts - 10, host.ThrottledJoinAttempts);
        Assert.True(host.IsConnectionJoined(OldConnection));
        Assert.False(host.TryTakeTakenOverConnection(out _));

        // The holder's client crashes. The service keeps sweeping; eight seconds later the same
        // client reconnects on a fresh connection and takes its session back.
        for (int i = 0; i < 80; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            host.ObserveInboundFrames(OldConnection, frames);
        }

        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("client-old", PlayerName))));
        Assert.True(host.IsConnectionJoined(NewConnection));
        Assert.True(host.TryTakeTakenOverConnection(out string closed));
        Assert.Equal(OldConnection, closed);
    }

    [Fact]
    public void ThrottledConnection_IsHeardAgainAfterItsInterval_AndCanJoinOnceTheHolderIsStale()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage("session-a", PlayerName));

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("session-a", PlayerName))));
        clock.Advance(TimeSpan.FromSeconds(1.9));
        // The holder has been silent for 7.9 s; the throttle drops the attempt unanswered anyway.
        Assert.Empty(host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("session-a", PlayerName)));
        Assert.Equal(1, host.ThrottledJoinAttempts);

        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("session-a", PlayerName))));
        Assert.True(host.IsConnectionJoined(NewConnection));
        Assert.False(host.IsJoinThrottled(NewConnection, "session-a", PlayerName));
        Assert.Equal(0, host.JoinThrottleEntryCount);
    }

    [Fact]
    public void DeferredJoinSpam_IsAnsweredOncePerThrottleInterval()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock, deferJoins: true);
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage("session-a", PlayerName));
        Assert.Single(TickAndDrain(host));
        Assert.True(host.IsConnectionJoined(OldConnection));

        for (int i = 0; i < 5; i++)
        {
            Assert.Empty(host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("session-a", PlayerName)));
        }

        // A connection has at most one queued join (the rest are ignored as duplicates).
        PendingConnectionReply reply = Assert.Single(TickAndDrain(host));
        Assert.Equal(NewConnection, reply.ConnectionId);
        Assert.IsType<JoinRejectedMessage>(reply.Message);
        Assert.Equal(0, host.ThrottledJoinAttempts);

        // Once rejected, attempts inside the throttle interval are not even queued.
        for (int i = 0; i < 5; i++)
        {
            Assert.Empty(host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("session-a", PlayerName)));
            Assert.Empty(TickAndDrain(host));
        }

        Assert.Equal(5, host.ThrottledJoinAttempts);
        Assert.True(host.IsConnectionJoined(OldConnection));

        // After the interval the next attempt is applied (and rejected) again.
        clock.Advance(ServerRoutingContext.JoinThrottleInitialInterval);
        Assert.Empty(host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("session-a", PlayerName)));
        reply = Assert.Single(TickAndDrain(host));
        Assert.IsType<JoinRejectedMessage>(reply.Message);
        Assert.Equal(5, host.ThrottledJoinAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TickHitch_DoesNotMakeAHealthyIdleClientLookStale(bool deferJoins)
    {
        // The service sweeps frame counts once per tick, so a keepalive that arrives while the tick
        // thread is stalled is only seen after the stall. A join routed right after the stall (on
        // the receive thread, or in the catch-up tick for deferred joins) must not read the stall
        // as the holder's silence.
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock, deferJoins);
        host.HandleClientMessage(OldConnection, new ClientJoinRequestMessage("client-old", PlayerName));
        if (deferJoins)
        {
            TickAndDrain(host);
        }

        Assert.True(host.IsConnectionJoined(OldConnection));
        long frames = 1;
        for (int i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            frames++;
            host.ObserveInboundFrames(OldConnection, frames);
        }

        // A 9 s stall: past both takeover windows, under the idle timeout. The holder kept sending
        // keepalives, which nobody has observed yet.
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.IsType<JoinRejectedMessage>(Assert.Single(
            Join(host, NewConnection, "client-old", PlayerName, deferJoins)));
        Assert.IsType<JoinRejectedMessage>(Assert.Single(
            Join(host, "connection-third", "client-third", PlayerName, deferJoins)));
        Assert.True(host.IsConnectionJoined(OldConnection));
        Assert.False(host.TryTakeTakenOverConnection(out _));

        // The sweep resumes and sees the keepalives that arrived during the stall.
        frames += 9;
        host.ObserveInboundFrames(OldConnection, frames);
        Assert.IsType<JoinRejectedMessage>(Assert.Single(
            Join(host, "connection-fourth", "client-old", PlayerName, deferJoins)));
        Assert.True(host.IsConnectionJoined(OldConnection));

        // A holder that really went silent is still taken over once sweeps show it.
        for (int i = 0; i < 80; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            host.ObserveInboundFrames(OldConnection, frames);
        }

        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            Join(host, "connection-fifth", "client-old", PlayerName, deferJoins)));
        Assert.True(host.TryTakeTakenOverConnection(out string closed));
        Assert.Equal(OldConnection, closed);
    }

    [Fact]
    public void CrashRejoin_SameSessionId_AfterTheTakeoverWindow_TakesOverTheSeatAndProfile()
    {
        using PersistenceTestDatabase database = new();
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreatePersistentHost(database, clock);
        WorldSessionRuntime runtime = host.Runtime!;
        JoinAndTick(host, OldConnection, "session-a", PlayerName);
        Assert.Equal("session-a", runtime.PrimaryRemoteSessionId?.Value);
        runtime.Player.Combatant.ApplyProgression(4, 25);

        // The client crashed 8 s ago and comes back, inside the 10 s idle timeout.
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

        clock.Advance(TimeSpan.FromSeconds(7.9));
        host.ObserveInboundFrames(OldConnection, 2);
        clock.Advance(TimeSpan.FromSeconds(7.9));
        host.ObserveInboundFrames(OldConnection, 2);
        Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage(NewConnection, new ClientJoinRequestMessage("session-a", PlayerName))));

        // An unchanged count is no traffic: the old connection goes stale. (A fresh connection:
        // the rejected one is throttled.)
        clock.Advance(TimeSpan.FromSeconds(0.1));
        host.ObserveInboundFrames(OldConnection, 2);
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage("connection-third", new ClientJoinRequestMessage("session-a", PlayerName))));
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

    [Theory]
    [InlineData("127.0.0.1:5000", "127.0.0.1")]
    [InlineData("10.1.2.3:65535", "10.1.2.3")]
    [InlineData("[::1]:5000", "::1")]
    [InlineData("[::ffff:10.0.0.7]:5000", "10.0.0.7")]
    [InlineData("connection-a", "connection-a")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("1", "1")]
    public void JoinThrottleSource_IsTheRemoteAddressOfAnIpEndpointId_ElseTheConnectionId(string connectionId, string source)
    {
        Assert.Equal(source, JoinThrottleTable.ResolveSource(connectionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconnectSpam_FromOneAddress_IsThrottledAcrossConnections_AndNeverQueuesTheJoin(bool deferJoins)
    {
        // Reviewer S4: each spam attempt comes on a fresh connection (a new source port). The
        // throttle follows the address, so a reconnect does not reset it, and a throttled join is
        // answered once without being claimed or queued (no persisted-player prefetch).
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock, deferJoins);
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(Join(host, "10.0.0.1:4000", "client-victim", PlayerName, deferJoins)));
        long frames = 1;
        host.ObserveInboundFrames("10.0.0.1:4000", frames);

        int processed = 0;
        int throttled = 0;
        int attempts = 0;
        for (int i = 0; i <= 300; i++)
        {
            if (i > 0)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            if (i % 10 == 0)
            {
                frames++;
            }

            host.ObserveInboundFrames("10.0.0.1:4000", frames);
            string spammer = $"10.0.0.2:{20000 + i}";
            attempts++;
            IReadOnlyList<IServerToClientMessage> immediate =
                host.HandleClientMessage(spammer, new ClientJoinRequestMessage($"client-spam-{i}", PlayerName));
            if (immediate.Count > 0)
            {
                JoinRejectedMessage refusal = Assert.IsType<JoinRejectedMessage>(Assert.Single(immediate));
                if (refusal.Reason.StartsWith(DedicatedServerHost.JoinThrottledRejectionReason, StringComparison.Ordinal))
                {
                    throttled++;
                    if (deferJoins)
                    {
                        Assert.Empty(TickAndDrain(host));
                    }

                    host.ReleaseConnection(spammer);
                    continue;
                }

                Assert.False(deferJoins);
                processed++;
            }
            else
            {
                Assert.True(deferJoins);
                PendingConnectionReply reply = Assert.Single(TickAndDrain(host));
                Assert.Equal(spammer, reply.ConnectionId);
                JoinRejectedMessage rejected = Assert.IsType<JoinRejectedMessage>(reply.Message);
                Assert.False(rejected.Reason.StartsWith(DedicatedServerHost.JoinThrottledRejectionReason, StringComparison.Ordinal));
                processed++;
            }

            host.ReleaseConnection(spammer);
        }

        // Processed at 0, 2, 6, 14 and 24 s (intervals 2, 4, 8, 10, 10 s); every other fresh
        // connection got one throttle refusal.
        Assert.Equal(5, processed);
        Assert.Equal(attempts - 5, throttled);
        Assert.Equal(attempts - 5, host.ThrottledJoinAttempts);
        Assert.True(host.IsConnectionJoined("10.0.0.1:4000"));
        Assert.False(host.TryTakeTakenOverConnection(out _));
        Assert.True(host.IsJoinThrottled("10.0.0.2:30000", "client-other", PlayerName));

        // The spam does not lock the victim out of its own name from its own address: it crashes
        // and rejoins on a new connection once the takeover window has passed.
        for (int i = 0; i < 80; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            host.ObserveInboundFrames("10.0.0.1:4000", frames);
        }

        Assert.IsType<JoinAcceptedMessage>(Assert.Single(Join(host, "10.0.0.1:4001", "client-victim-2", PlayerName, deferJoins)));
        Assert.True(host.TryTakeTakenOverConnection(out string closed));
        Assert.Equal("10.0.0.1:4000", closed);
    }

    [Fact]
    public void ReconnectSpam_WithTheVictimsSessionId_FromOneAddress_IsThrottledAcrossConnections()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        host.HandleClientMessage("10.0.0.1:4000", new ClientJoinRequestMessage("client-victim", PlayerName));

        JoinRejectedMessage rejected = Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage("10.0.0.2:20000", new ClientJoinRequestMessage("client-victim", "Spammer"))));
        Assert.Equal(DedicatedServerHost.SessionInUseRejectionReason, rejected.Reason);
        host.ReleaseConnection("10.0.0.2:20000");

        // Another name does not dodge it: the session id is what is contested.
        clock.Advance(TimeSpan.FromSeconds(1));
        rejected = Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage("10.0.0.2:20001", new ClientJoinRequestMessage("client-victim", "Spammer 2"))));
        Assert.StartsWith(DedicatedServerHost.JoinThrottledRejectionReason, rejected.Reason);
        Assert.Equal(1, host.ThrottledJoinAttempts);
        // A further join on the connection that was already answered is dropped unanswered.
        Assert.Empty(host.HandleClientMessage("10.0.0.2:20001", new ClientJoinRequestMessage("client-victim", "Spammer 2")));
        Assert.Equal(2, host.ThrottledJoinAttempts);

        // Another address is not throttled by it.
        clock.Advance(TimeSpan.FromSeconds(0.5));
        rejected = Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage("10.0.0.3:20000", new ClientJoinRequestMessage("client-victim", "Spammer"))));
        Assert.Equal(DedicatedServerHost.SessionInUseRejectionReason, rejected.Reason);
        Assert.Equal(2, host.ThrottledJoinAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManyClientsFromOneAddress_WithDifferentNames_AreNeverThrottled(bool deferJoins)
    {
        // A loopback test server, or players behind one NAT: one address, many clients.
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock, deferJoins);
        for (int i = 0; i < 6; i++)
        {
            Assert.IsType<JoinAcceptedMessage>(Assert.Single(
                Join(host, $"127.0.0.1:{50000 + i}", $"client-{i}", $"Player {i}", deferJoins)));
        }

        // One of them restarts and rejoins while its old connection is still live: rejected, and
        // that name is throttled for the address.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsType<JoinRejectedMessage>(Assert.Single(Join(host, "127.0.0.1:50100", "client-0b", "Player 0", deferJoins)));
        host.ReleaseConnection("127.0.0.1:50100");
        Assert.True(host.IsJoinThrottled("127.0.0.1:50101", "client-0c", "Player 0"));

        // Other names from the same address still join at once.
        for (int i = 6; i < 12; i++)
        {
            Assert.IsType<JoinAcceptedMessage>(Assert.Single(
                Join(host, $"127.0.0.1:{50000 + i}", $"client-{i}", $"Player {i}", deferJoins)));
        }

        Assert.Equal(0, host.ThrottledJoinAttempts);
        Assert.Equal(12, host.ConnectionCount);
    }

    [Fact]
    public void JoinThrottleState_Expires_AMaxIntervalAfterItsIntervalEnds()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        host.HandleClientMessage("10.0.0.1:4000", new ClientJoinRequestMessage("client-victim", PlayerName));
        long frames = 1;

        Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage("10.0.0.2:20000", new ClientJoinRequestMessage("client-spam", PlayerName))));
        Assert.Equal(1, host.JoinThrottleEntryCount);

        // Past the 2 s interval the entry is still remembered: the next rejection doubles it.
        AdvanceKeepingAlive(TimeSpan.FromSeconds(2));
        Assert.False(host.IsJoinThrottled("10.0.0.2:20001", "client-spam", PlayerName));
        Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage("10.0.0.2:20001", new ClientJoinRequestMessage("client-spam", PlayerName))));
        AdvanceKeepingAlive(TimeSpan.FromSeconds(3.9));
        Assert.True(host.IsJoinThrottled("10.0.0.2:20002", "client-spam", PlayerName));
        AdvanceKeepingAlive(TimeSpan.FromSeconds(0.1));
        Assert.False(host.IsJoinThrottled("10.0.0.2:20002", "client-spam", PlayerName));
        Assert.Equal(1, host.JoinThrottleEntryCount);

        // A full max interval after the 4 s interval ended, the entry is forgotten, and the next
        // rejection starts over at the initial interval.
        AdvanceKeepingAlive(ServerRoutingContext.JoinThrottleMaxInterval);
        Assert.False(host.IsJoinThrottled("10.0.0.2:20003", "client-spam", PlayerName));
        Assert.Equal(0, host.JoinThrottleEntryCount);
        Assert.IsType<JoinRejectedMessage>(Assert.Single(
            host.HandleClientMessage("10.0.0.2:20003", new ClientJoinRequestMessage("client-spam", PlayerName))));
        AdvanceKeepingAlive(ServerRoutingContext.JoinThrottleInitialInterval - TimeSpan.FromSeconds(0.1));
        Assert.True(host.IsJoinThrottled("10.0.0.2:20004", "client-spam", PlayerName));
        AdvanceKeepingAlive(TimeSpan.FromSeconds(0.1));
        Assert.False(host.IsJoinThrottled("10.0.0.2:20004", "client-spam", PlayerName));
        Assert.True(host.IsConnectionJoined("10.0.0.1:4000"));

        void AdvanceKeepingAlive(TimeSpan duration)
        {
            clock.Advance(duration);
            host.ObserveInboundFrames("10.0.0.1:4000", ++frames);
        }
    }

    [Fact]
    public void JoinThrottleTable_StaysBounded_AndPrunesExpiredEntries()
    {
        JoinThrottleTable table = new();
        long frequency = TimeSpan.TicksPerSecond;
        long now = 1;
        for (int i = 0; i < JoinThrottleTable.MaxEntries * 3; i++)
        {
            table.Throttle(JoinThrottleTable.PlayerNameKey($"10.0.{i / 256}.{i % 256}", "Victim"), now, frequency, out _);
            now += TimeSpan.TicksPerMillisecond;
            Assert.True(table.Count <= JoinThrottleTable.MaxEntries);
        }

        Assert.Equal(JoinThrottleTable.MaxEntries, table.Count);
        // The newest entry survived the evictions.
        JoinThrottleTable.Key newest = JoinThrottleTable.PlayerNameKey(
            $"10.0.{(JoinThrottleTable.MaxEntries * 3 - 1) / 256}.{(JoinThrottleTable.MaxEntries * 3 - 1) % 256}", "VICTIM");
        Assert.True(table.IsThrottled(newest, now, frequency));

        // Once every entry has expired, one new entry prunes them all.
        now += (long)((ServerRoutingContext.JoinThrottleInitialInterval + ServerRoutingContext.JoinThrottleMaxInterval).TotalSeconds * frequency);
        table.Throttle(JoinThrottleTable.SessionKey("10.9.9.9", "client-x"), now, frequency, out _);
        Assert.Equal(1, table.Count);
    }

    private static async Task AdvanceUntilAsync(DedicatedServerService service, Func<bool> condition)
    {
        for (int i = 0; i < 64 && !condition(); i++)
        {
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        }

        Assert.True(condition());
    }

    private static DedicatedServerHost CreateHost(ManualTimeProvider clock, bool deferJoins = false)
    {
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(deferJoinsToTick: deferJoins);
        host.TimeProvider = clock;
        return host;
    }

    private static List<PendingConnectionReply> TickAndDrain(DedicatedServerHost host)
    {
        List<PendingConnectionReply> drained = [];
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        host.DrainPendingJoinRepliesInto(drained);
        return drained;
    }

    // A join's replies, routed at once or (deferJoins) applied by the next tick.
    private static List<IServerToClientMessage> Join(
        DedicatedServerHost host,
        string connectionId,
        string sessionId,
        string displayName,
        bool deferJoins)
    {
        IReadOnlyList<IServerToClientMessage> immediate =
            host.HandleClientMessage(connectionId, new ClientJoinRequestMessage(sessionId, displayName));
        if (!deferJoins)
        {
            return [.. immediate];
        }

        Assert.Empty(immediate);
        return TickAndDrain(host)
            .Where(reply => reply.ConnectionId == connectionId)
            .Select(reply => reply.Message)
            .ToList();
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
