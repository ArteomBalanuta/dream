using System.Net;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;
using static Dream.Tests.Integration.ProtocolV2SharedSnapshotLoopbackIntegrationTests;

namespace Dream.Tests.Integration;

// NETR-1: a client that stops reading used to make its connection merge deltas without bound.
// Past the cap the merged deltas are dropped and the server serves that connection a full world
// instead; once the client reads again it converges on the server's world.
public class CoalesceOverflowResyncLoopbackTests(ITestOutputHelper output)
{
    // Ticks in a row with the slow connection's write blocked (no frame completed while frames
    // wait behind it) before builds start. Builds made while the socket still absorbed whole
    // frames went out one per frame and never piled up behind the cap, which made the overflow
    // depend on how fast the kernel buffers filled (a run once ended "never overflowed").
    private const int StalledTicksBeforeBuilds = 32;

    [Fact]
    public async Task SlowReader_PastTheCoalesceCap_GetsAWorldResync_AndConverges()
    {
        // Cap of one merged mutation: the second build merged behind a blocked write overflows.
        // Liveness is off: this client deliberately neither reads nor sends for a while.
        ServerConnectionOptions options = new()
        {
            MaxCoalescedWorldMutations = 1,
            IdleTimeout = TimeSpan.Zero,
            SendStallTimeout = TimeSpan.Zero
        };
        DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        try
        {
            await using DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer(),
                options);
            service.Start();
            (RawClient primary, ServerConnection primaryConnection) = await ConnectAndJoinAsync(service, host, PrimarySession, receiveBufferSize: null);
            await using RawClient primaryClient = primary;
            (RawClient slow, ServerConnection slowConnection) = await ConnectAndJoinAsync(service, host, LateSecondary, receiveBufferSize: 1024);
            await using RawClient slowClient = slow;
            ulong slowJoinTick = host.Runtime!.CurrentServerTick;
            slow.PauseReading();

            int builds = 0;
            int ticksAfterOverflow = 0;
            long lastFramesSent = -1;
            int stalledTicks = 0;
            for (int tick = 0; ticksAfterOverflow < 8; tick++)
            {
                Assert.True(
                    tick < 6000,
                    $"The slow connection never overflowed (coalesced {slowConnection.CoalescedSnapshots}, " +
                    $"builds {builds}, stalled ticks {stalledTicks}).");
                await primary.SendCommandAsync(walking: false);

                // The writer is blocked once its socket write stops completing while snapshots
                // queue (and merge) behind it. Only then does every build land in the one merged
                // entry, so the second build passes the cap of one for certain.
                long framesSent = slowConnection.FramesSent;
                if (framesSent == lastFramesSent && slowConnection.PendingOutboundCount > 0)
                {
                    stalledTicks++;
                }
                else
                {
                    stalledTicks = 0;
                    lastFramesSent = framesSent;
                }

                if (tick % 5 == 0 &&
                    builds < BuildCells.Length &&
                    slowConnection.OverflowResyncs == 0 &&
                    stalledTicks >= StalledTicksBeforeBuilds)
                {
                    Assert.True(host.TryEnqueueServerAction(new PlayerActionMessage(
                        new Dream.Multiplayer.Sessions.PlayerSessionId(PrimarySession),
                        (ulong)(builds + 1),
                        new BuildUseIntent(BuildCells[builds]))));
                    builds++;
                }

                await TickAsync(service, host);
                Assert.True(await OutboundDrain.FlushAsync(primaryConnection, ProtocolV2SharedSnapshotLoopbackIntegrationTests.Timeout));
                Assert.False(slowConnection.IsFaulted, "The slow connection faulted instead of resyncing.");
                ticksAfterOverflow += slowConnection.OverflowResyncs > 0 ? 1 : 0;
            }

            Assert.True(service.CaptureMetrics().CoalesceOverflowResyncs >= 1);
            slow.ResumeReading();
            Assert.True(await slowConnection.FlushOutboundAsync(ProtocolV2SharedSnapshotLoopbackIntegrationTests.Timeout), "The slow connection never drained.");
            ulong lastTick = host.Runtime!.CurrentServerTick;
            await slow.WaitForSnapshotTickAsync(lastTick);
            await primary.WaitForSnapshotTickAsync(lastTick);
            Assert.Null(slow.ReadFault);

            // The resync reached the slow client after it joined.
            Assert.Contains(
                slow.Snapshots(),
                frame => frame.Decoded.Message is ServerSnapshotMessage { Snapshot.World: not null } snapshot &&
                    snapshot.Snapshot.ServerTick > slowJoinTick);

            ulong serverChecksum = WorldStateChecksum.Compute(host.Runtime!.Map);
            ClientWorldReplay slowReplay = slow.Replay(host.Runtime!.Map.Width);
            ClientWorldReplay primaryReplay = primary.Replay(host.Runtime!.Map.Width);
            Assert.Equal(serverChecksum, slowReplay.FinalChecksum);
            Assert.Equal(serverChecksum, primaryReplay.FinalChecksum);
            Assert.Equal(0, primaryReplay.ChecksumMismatches);
            output.WriteLine(
                $"builds={builds} coalesced={slowConnection.CoalescedSnapshots} overflowResyncs={slowConnection.OverflowResyncs} " +
                $"slowMismatchesBeforeResync={slowReplay.ChecksumMismatches} ticks={lastTick}");
        }
        finally
        {
            host.Dispose();
        }
    }

    private static async Task<(RawClient Client, ServerConnection Connection)> ConnectAndJoinAsync(
        DedicatedServerService service,
        DedicatedServerHost host,
        string sessionId,
        int? receiveBufferSize)
    {
        Task<ServerConnection> accept = service.AcceptOnThreadPoolAsync();
        RawClient client = await RawClient.ConnectAsync(service.Port, sessionId, receiveBufferSize);
        ServerConnection connection = await accept.WaitAsync(ProtocolV2SharedSnapshotLoopbackIntegrationTests.Timeout);
        await client.HandshakeAsync();
        await WaitUntilAsync(() => host.IsConnectionJoined(connection.ConnectionId), $"{sessionId} never joined");
        await client.WaitForJoinAcceptedAsync();
        return (client, connection);
    }

    private static async Task TickAsync(DedicatedServerService service, DedicatedServerHost host)
    {
        ulong before = host.Runtime!.CurrentServerTick;
        int executed = await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);
        if (executed == 0)
        {
            executed = await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds * 0.01);
        }

        Assert.Equal(1, executed);
        Assert.Equal(before + 1, host.Runtime!.CurrentServerTick);
    }
}
