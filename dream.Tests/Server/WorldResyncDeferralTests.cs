using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Runtime;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Tests.Integration;

namespace Dream.Tests.Server;

// NETR-6: a world resync no longer exports the whole world on the receive thread under the tick
// lock. WorldResyncRequestHandler only marks the connection (at most once per 2 s), and
// DedicatedServerHost serves it after its next tick through the pending-reply drain.
public class WorldResyncDeferralTests
{
    private const string Connection = "connection-a";

    [Fact]
    public void ResyncRequest_GetsNoImmediateReply_AndIsServedAfterTheNextTick()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        Join(host);

        Assert.Empty(host.HandleClientMessage(Connection, new ClientWorldResyncRequestMessage(7)));
        Assert.Empty(DrainReplies(host));

        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);

        PendingConnectionReply reply = Assert.Single(DrainReplies(host));
        Assert.Equal(Connection, reply.ConnectionId);
        ServerSnapshotMessage snapshot = Assert.IsType<ServerSnapshotMessage>(reply.Message);
        Assert.NotNull(snapshot.Snapshot.World);
        Assert.Equal(host.Runtime!.CurrentServerTick, snapshot.Snapshot.ServerTick);

        // Served once: later ticks send nothing more.
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        Assert.Empty(DrainReplies(host));
    }

    [Fact]
    public void TwoRequests100MsApart_ProduceOneExport_AndTheCooldownEndsAfterTwoSeconds()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        Join(host);

        host.HandleClientMessage(Connection, new ClientWorldResyncRequestMessage(1));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        host.HandleClientMessage(Connection, new ClientWorldResyncRequestMessage(2));
        for (int i = 0; i < 3; i++)
        {
            host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        }

        Assert.Single(DrainReplies(host));

        // Still inside the 2 s window (1.9 s after the accepted request): dropped.
        clock.Advance(TimeSpan.FromMilliseconds(1800));
        host.HandleClientMessage(Connection, new ClientWorldResyncRequestMessage(3));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        Assert.Empty(DrainReplies(host));

        // The window has passed: the next request is served after the next tick.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        host.HandleClientMessage(Connection, new ClientWorldResyncRequestMessage(4));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        Assert.Single(DrainReplies(host));
    }

    [Fact]
    public void Cooldown_IsPerConnection()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);
        Join(host);
        host.HandleClientMessage("connection-b", new ClientJoinRequestMessage("session-b", "B"));

        host.HandleClientMessage(Connection, new ClientWorldResyncRequestMessage(1));
        host.HandleClientMessage("connection-b", new ClientWorldResyncRequestMessage(1));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);

        Assert.Equal(
            [Connection, "connection-b"],
            DrainReplies(host).Select(reply => reply.ConnectionId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void UnjoinedOrReleasedConnections_AreNotServed()
    {
        ManualTimeProvider clock = new();
        using DedicatedServerHost host = CreateHost(clock);

        host.HandleClientMessage("connection-unjoined", new ClientWorldResyncRequestMessage(1));
        Join(host);
        host.HandleClientMessage(Connection, new ClientWorldResyncRequestMessage(1));
        host.ReleaseConnection(Connection);
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);

        Assert.Empty(DrainReplies(host));
    }

    [Fact]
    public void Context_Cooldown_UsesTheGivenTimestampFrequency()
    {
        ServerRoutingContext context = new("connection");

        Assert.True(context.TryRequestWorldResync(nowTimestamp: 1_000, timestampFrequency: 1_000));
        Assert.True(context.WorldResyncPending);
        Assert.False(context.TryRequestWorldResync(nowTimestamp: 2_999, timestampFrequency: 1_000));
        Assert.True(context.TryTakeWorldResyncRequest());
        Assert.False(context.TryTakeWorldResyncRequest());
        Assert.True(context.TryRequestWorldResync(nowTimestamp: 3_000, timestampFrequency: 1_000));
    }

    [Fact]
    public async Task OverLoopback_TwoQuickRequests_GetOneWorldSnapshot_DeliveredAfterTheTick()
    {
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(
            ServerBootstrap.CreateAuthoritativeHost(persistence: null));
        await using WireLoopbackClient client = await server.ConnectAndJoinAsync("session-a");
        string connectionId = server.ConnectionFor(client).ConnectionId;
        Assert.True(server.Host.TryGetConnectionContext(connectionId, out ServerRoutingContext? context));

        await client.SendAsync(new ClientWorldResyncRequestMessage(0));
        await WaitUntilAsync(() => context!.WorldResyncPending, "the resync request was never marked");
        await Task.Delay(100);
        await client.SendAsync(new ClientWorldResyncRequestMessage(0));
        // Commands on one connection are handled in order, so once this one is seen the second
        // resync request has been handled (and dropped by the cooldown) too.
        await client.SendCommandAsync(PlayerInteractionMode.Combat, PlayerMoveIntent.None, OpenTK.Mathematics.Vector3.UnitZ, attack: false);
        ulong commandSequence = await WaitForCommandAsync(context!);

        // Nothing went out on the receive path.
        Assert.True(await server.FlushAllAsync());
        Assert.DoesNotContain(client.Frames, IsWorldSnapshot);

        ulong tickBefore = server.Runtime.CurrentServerTick;
        for (int i = 0; i < 3; i++)
        {
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }

        await client.WaitForSnapshotTickAsync(server.Runtime.CurrentServerTick);
        await client.WaitForAsync(frames => frames.Any(IsWorldSnapshot), "world resync snapshot");
        WireFrame world = Assert.Single(client.Frames, IsWorldSnapshot);
        Assert.Equal(tickBefore + 1, ((ServerSnapshotMessage)world.Decoded.Message).Snapshot.ServerTick);
        Assert.True(commandSequence > 0);
    }

    private static bool IsWorldSnapshot(WireFrame frame)
    {
        // Join replies carry the world in JoinAccepted, so any snapshot with a world is a resync.
        return frame.Decoded.Message is ServerSnapshotMessage { Snapshot.World: not null };
    }

    private static async Task<ulong> WaitForCommandAsync(ServerRoutingContext context)
    {
        await WaitUntilAsync(() => context.LastCommandMessage != null, "the command after the resync requests never arrived");
        return context.LastCommandMessage!.CommandFrame.Sequence;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(5);
        }
    }

    private static DedicatedServerHost CreateHost(TimeProvider clock)
    {
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler(clock)
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(RuntimeAuthoritativeSessionHost.CreateDefault(), router);
    }

    private static void Join(DedicatedServerHost host)
    {
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage(Connection, new ClientJoinRequestMessage("session-a", "A"))));
    }

    private static List<PendingConnectionReply> DrainReplies(DedicatedServerHost host)
    {
        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        return replies;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan by) => _timestamp += by.Ticks;
    }
}
