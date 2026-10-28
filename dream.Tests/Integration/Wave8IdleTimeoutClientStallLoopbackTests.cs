using System.Diagnostics;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Integration;

// Wave 8 tester: NETR-1's idle timeout (ServerConnectionOptions.IdleTimeout, 10 s, no inbound frame)
// against a real client that, since NETR-4, sends only from prediction ticks. There is no client
// keepalive yet (NETR-2).
//
// - Menus: PlayScene.Update keeps running with gameplay input off and still produces one (idle)
//   command per prediction tick (PlayScene.cs:186-206), so an open menu is never idle.
// - A frozen game loop (a long GC, a debugger break, a Windows modal move/resize loop while the
//   title bar is held) sends nothing: the session's receive task keeps draining the socket, so the
//   server never sees a send stall, but its idle clock runs. Measured here with the default options
//   of the integrated server: 3 s and 8 s survive (the threshold is 10 s, not 8 s), 11 s is kicked.
//   The client only sees the socket close: DisconnectReason stays null, so the player gets no reason. Holding a window's title bar for over 10 s disconnects the
//   player until NETR-2 adds a keepalive sent from the session's own thread.
public class Wave8IdleTimeoutClientStallLoopbackTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ClientLoopStall_ThreeSeconds_StaysConnected()
    {
        StallOutcome outcome = await RunAsync(TimeSpan.FromSeconds(3), "w8-idle-3");
        output.WriteLine(outcome.ToString());
        Assert.True(outcome.ConnectedAfter, outcome.ToString());
        Assert.True(outcome.SnapshotsAfterResume > 0, outcome.ToString());
    }

    [Fact]
    public async Task ClientLoopStall_EightSeconds_StaysConnectedUnderTheTenSecondIdleTimeout()
    {
        StallOutcome outcome = await RunAsync(TimeSpan.FromSeconds(8), "w8-idle-8");
        output.WriteLine(outcome.ToString());
        Assert.True(outcome.ConnectedAfter, outcome.ToString());
        Assert.True(outcome.SnapshotsAfterResume > 0, outcome.ToString());
    }

    // Expected and documented (no client keepalive until NETR-2).
    [Fact]
    public async Task ClientLoopStall_ElevenSeconds_IsKickedForIdleTimeout()
    {
        StallOutcome outcome = await RunAsync(TimeSpan.FromSeconds(11), "w8-idle-11");
        output.WriteLine(outcome.ToString());
        Assert.False(outcome.ConnectedAfter, outcome.ToString());
    }

    private static async Task<StallOutcome> RunAsync(TimeSpan stall, string sessionId)
    {
        Assert.Equal(TimeSpan.FromSeconds(10), ServerConnectionOptions.Default.IdleTimeout);
        await using IntegratedLocalServerHandle server = await IntegratedLocalServerHandle.StartAsync(
            0,
            () => DedicatedServerHost.CreateRuntimeAttached(persistence: null));
        Assert.True(server.IsReady, server.ErrorMessage);

        RemoteClientSession session = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.BoundPort, sessionId, sessionId, "idle-test"));
        await using (session)
        {
            PlayerSessionId remoteId = new(sessionId);
            ulong tick = 0;
            Stopwatch clock = Stopwatch.StartNew();
            bool joined = false;
            while (!joined && clock.Elapsed < Deadline)
            {
                while (session.TryDequeueInbound(out IServerToClientMessage? message))
                {
                    joined |= message is JoinAcceptedMessage;
                }

                await Task.Delay(5);
            }

            Assert.True(joined, "join was never accepted");

            // Half a second of normal play: one command per 64 Hz tick.
            await SendTicksAsync(session, remoteId, () => ++tick, TimeSpan.FromMilliseconds(500));

            // The game loop freezes: nothing is sent and nothing is drained.
            await Task.Delay(stall);

            // Resume; give the server a second to tick, check liveness and answer.
            while (session.TryDequeueInbound(out _))
            {
            }

            int snapshotsAfter = await SendTicksAsync(session, remoteId, () => ++tick, TimeSpan.FromSeconds(1.5));
            return new StallOutcome(stall, session.IsConnected, session.DisconnectReason, snapshotsAfter);
        }
    }

    private static async Task<int> SendTicksAsync(RemoteClientSession session, PlayerSessionId remoteId, Func<ulong> nextTick, TimeSpan duration)
    {
        int snapshots = 0;
        Stopwatch clock = Stopwatch.StartNew();
        double sentUntil = 0d;
        while (clock.Elapsed < duration && session.IsConnected)
        {
            while (sentUntil <= clock.Elapsed.TotalSeconds)
            {
                PlayerCommandFrame command = new(
                    remoteId,
                    nextTick(),
                    PlayerModeSwitchIntent.None,
                    PlayerMoveIntent.None,
                    new PlayerLookIntent(0f, 0f, Vector3.UnitZ),
                    PrimaryUsePressed: false);
                session.QueueCommandFrame(RemotePlayerCommandRelay.CreateRelayCommand(command, remoteId, Vector3.UnitZ));
                sentUntil += SimulationClock.FixedDeltaSeconds;
            }

            while (session.TryDequeueInbound(out IServerToClientMessage? message))
            {
                if (message is ServerSnapshotMessage)
                {
                    snapshots++;
                }
            }

            await Task.Delay(5);
        }

        return snapshots;
    }

    private sealed record StallOutcome(TimeSpan Stall, bool ConnectedAfter, string? DisconnectReason, int SnapshotsAfterResume);
}
