using Xunit;
using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Integration;

// T1b over real loopback TCP: a client that applies join-accepted like PlayScene does (Apply the
// anchor, or Clear without one) reads the same world time as the server's WorldClock at every
// snapshot tick it receives, within 1.1 ticks. Also after a full world resync (which carries no
// clock), after a reconnect (which re-anchors), and with a non-zero persisted world time.
public class WorldClockReplicationLoopbackTests
{
    private const double ToleranceTicks = 1.1d;

    [Fact]
    public async Task AfterJoin_TheClientClock_MatchesTheServerClock()
    {
        await using ClockServer server = await ClockServer.StartAsync(persistedWorldTicks: null);
        await using WireLoopbackClient client = await server.Wire.ConnectAndJoinAsync("clock-a");

        await server.TickAsync(40);

        await server.AssertClientMatchesAsync(client, minimumComparedTicks: 30);
    }

    [Fact]
    public async Task AfterAWorldResync_TheClientClock_StillMatchesTheServerClock()
    {
        await using ClockServer server = await ClockServer.StartAsync(persistedWorldTicks: null);
        await using WireLoopbackClient client = await server.Wire.ConnectAndJoinAsync("clock-a");
        await server.TickAsync(10);

        await client.SendAsync(new ClientWorldResyncRequestMessage(0));
        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (!client.Frames.Any(frame => frame.Decoded.Message is ServerSnapshotMessage { WorldTransferId: not null }))
        {
            Assert.True(DateTime.UtcNow < deadline, "the world resync never arrived");
            await server.TickAsync(1);
        }

        await server.TickAsync(30);

        // The resync is a snapshot: it leaves the join anchor alone.
        Assert.Single(client.Frames, frame => frame.Decoded.Message is JoinAcceptedMessage);
        await server.AssertClientMatchesAsync(client, minimumComparedTicks: 30);
    }

    [Fact]
    public async Task AfterAReconnect_TheNewJoin_ReanchorsTheClientClock()
    {
        await using ClockServer server = await ClockServer.StartAsync(persistedWorldTicks: null);
        await using WireLoopbackClient stay = await server.Wire.ConnectAndJoinAsync("clock-stay");
        WireLoopbackClient first = await server.Wire.ConnectAndJoinAsync("clock-returning");
        await server.TickAsync(20);
        await server.AssertClientMatchesAsync(first, minimumComparedTicks: 15);
        await server.Wire.DisconnectAsync(first);

        await server.TickAsync(25);
        await using WireLoopbackClient second = await server.Wire.ConnectAndJoinAsync("clock-returning");
        await server.TickAsync(20);

        JoinAcceptedMessage rejoin = Assert.Single(
            WorldTransferReassembler.Reassemble(second.Frames.Select(frame => frame.Decoded.Message)).OfType<JoinAcceptedMessage>());
        Assert.NotNull(rejoin.WorldClock);
        Assert.True(rejoin.WorldClock!.Value.ServerTick > first.Frames
            .Select(frame => frame.Decoded.Message).OfType<JoinAcceptedMessage>().Single().CurrentServerTick);
        await server.AssertClientMatchesAsync(second, minimumComparedTicks: 15);
        await server.AssertClientMatchesAsync(stay, minimumComparedTicks: 60);
    }

    // A persisted world far past its first day: the anchor carries the world time, not the tick.
    [Fact]
    public async Task WithANonZeroPersistedWorldTime_TheClientClock_MatchesTheServerClock()
    {
        long persisted = (3 * WorldClockSettings.Default.TicksPerDay) + 12_345;
        await using ClockServer server = await ClockServer.StartAsync(persistedWorldTicks: persisted);
        await using WireLoopbackClient client = await server.Wire.ConnectAndJoinAsync("clock-a");

        await server.TickAsync(30);

        JoinAcceptedMessage join = Assert.Single(
            WorldTransferReassembler.Reassemble(client.Frames.Select(frame => frame.Decoded.Message)).OfType<JoinAcceptedMessage>());
        Assert.True(join.WorldClock!.Value.WorldTicks >= persisted);
        ClientWorldClock clock = new();
        clock.Apply(join.WorldClock.Value);
        Assert.True(clock.DayNumber(join.CurrentServerTick) >= 3);
        await server.AssertClientMatchesAsync(client, minimumComparedTicks: 25);
    }

    private sealed class ClockServer : IAsyncDisposable
    {
        // The server's world ticks after each tick this test ran (joins may run ticks of their own,
        // which are not compared).
        private readonly Dictionary<ulong, long> _serverWorldTicks = [];

        private ClockServer(WireLoopbackServer wire)
        {
            Wire = wire;
            Record();
        }

        public WireLoopbackServer Wire { get; }

        private WorldSessionRuntime Runtime => Wire.Runtime;

        public static async Task<ClockServer> StartAsync(long? persistedWorldTicks)
        {
            DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(persistence: null);
            if (persistedWorldTicks is long worldTicks)
            {
                // What a restart from a persisted world gives the runtime before its first tick.
                host.Runtime!.ConfigureWorldClock(WorldClockSettings.Default, worldTicks);
            }

            return new ClockServer(await WireLoopbackServer.StartAsync(host));
        }

        public async Task TickAsync(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                await Wire.TickAsync();
                Record();
                Assert.True(await Wire.FlushAllAsync());
            }
        }

        // Replays the client's frames like PlayScene: a join applies (or clears) the anchor, and
        // each snapshot tick is the client's server tick estimate at that moment.
        public async Task AssertClientMatchesAsync(WireLoopbackClient client, int minimumComparedTicks)
        {
            ulong lastTick = Runtime.CurrentServerTick;
            Assert.True(await Wire.FlushAllAsync());
            await client.WaitForSnapshotTickAsync(lastTick);
            Assert.Null(client.ReadFault);

            ClientWorldClock clock = new();
            long ticksPerDay = Runtime.WorldClock.Settings.TicksPerDay;
            long startOffset = Runtime.WorldClock.Settings.StartOffsetTicks;
            int compared = 0;
            foreach (IServerToClientMessage message in WorldTransferReassembler.Reassemble(client.Frames.Select(frame => frame.Decoded.Message)))
            {
                switch (message)
                {
                    case JoinAcceptedMessage join:
                        if (join.WorldClock is { } anchor)
                        {
                            clock.Apply(anchor);
                        }
                        else
                        {
                            clock.Clear();
                        }

                        Assert.True(clock.HasAnchor, "the join carried no world clock");
                        break;
                    case ServerSnapshotMessage snapshot when _serverWorldTicks.TryGetValue(snapshot.Snapshot.ServerTick, out long serverWorldTicks):
                        double serverTime = (double)((serverWorldTicks + startOffset) % ticksPerDay) / ticksPerDay;
                        double clientTime = clock.TimeOfDay01(snapshot.Snapshot.ServerTick);
                        double difference = Math.Abs(clientTime - serverTime);
                        double differenceTicks = Math.Min(difference, 1d - difference) * ticksPerDay;
                        Assert.True(
                            differenceTicks <= ToleranceTicks,
                            $"{client.SessionId} at tick {snapshot.Snapshot.ServerTick}: client {clientTime:F8}, server {serverTime:F8} ({differenceTicks:F3} ticks apart)");
                        Assert.Equal(
                            Math.Floor((double)(serverWorldTicks + startOffset) / ticksPerDay),
                            clock.DayNumber(snapshot.Snapshot.ServerTick));
                        compared++;
                        break;
                }
            }

            Assert.True(compared >= minimumComparedTicks, $"{client.SessionId}: only {compared} snapshot ticks compared");
        }

        public ValueTask DisposeAsync() => Wire.DisposeAsync();

        private void Record() => _serverWorldTicks[Runtime.CurrentServerTick] = Runtime.WorldClock.WorldTicks;
    }
}
