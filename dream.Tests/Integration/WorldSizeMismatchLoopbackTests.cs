using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Worlds;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Integration;

// Persistence wave B x protocol v7: a client whose local floor size differs from the server's world
// no longer decides the map size. A real wire join to a 30-cell world 2 carries the world
// descriptor, and a client session built for a 20-cell floor ends up with a 30x8x30 map after
// applying the join, before any snapshot, and keeps it through the world transfer that follows.
public sealed class WorldSizeMismatchLoopbackTests(ITestOutputHelper output)
{
    private const string Session = "mismatch";
    private const int ServerWorldSize = 30;
    private const int ClientFloorSize = 20;

    [Fact]
    public async Task ClientWithADifferentFloorSize_TakesTheServersWorldSize_FromTheJoin()
    {
        using MigrationTestDatabase database = new();
        WorldRecord world = WorldBundles.OpenCatalog(database).Create(new WorldCreateOptions("wide", ServerWorldSize));
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(
            ServerBootstrap.CreateAuthoritativeHost(WorldBundles.Bind(database.Schema, world)));
        await using WireLoopbackClient wire = await server.ConnectAndJoinAsync(Session);

        JoinAcceptedMessage join = wire.Frames.Select(frame => frame.Decoded.Message).OfType<JoinAcceptedMessage>().First();
        Assert.NotNull(join.World);
        WorldDescriptorWire descriptor = join.World!;
        Assert.Equal(world.Descriptor, descriptor.ToDescriptor());
        Assert.Equal(new WorldDescriptorWire(world.Descriptor.Id.Value, "wide", ServerWorldSize, 8, ServerWorldSize, "generated", "natural", 1), descriptor);

        using ClientWorldSession client = new(ClientFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        Assert.Equal(ClientFloorSize, client.Map.Width);

        ClientJoinAcceptedApplier.ApplyToRuntime(client, join);
        Assert.Equal(ServerWorldSize, client.Map.Width);
        Assert.Equal(ServerWorldSize, client.Map.Depth);
        Assert.Equal(8, client.Map.Height);

        // The world transfer that follows (a full resync) leaves the size alone.
        //
        // The wait has to keep TICKING the server, which is why it is UntilAsync and not the client's
        // own WaitForAsync. A resync is not served on the receive thread: WorldResyncRequestHandler
        // only marks the connection, and DedicatedServerHost.ServePendingWorldResyncs exports the
        // world at the end of the NEXT tick. WireLoopbackServer.TickAsync does not wait for this
        // message either - it only syncs on the last command and action sequence, and this test sends
        // neither - so a fixed run of ticks can finish before the server's receive loop has even read
        // the request. Nothing then ticks the server again, no further frame can arrive, and a
        // frames-only wait sits out its whole no-progress window: that is the 30 s timeout this test
        // was seen taking under load, where alone it passes in ~300 ms. Bounding on the server's ticks
        // AND on this client's received frames, with the loop driving both, makes the wait outlive a
        // late read and still fail cheaply (~9 s) if the transfer genuinely never comes.
        await wire.SendAsync(new ClientWorldResyncRequestMessage(0));
        await server.UntilAsync(
            () => WorldTransferReassembler.Reassemble(wire.Frames.Select(frame => frame.Decoded.Message))
                .Any(message => message is ServerSnapshotMessage { Snapshot.World: not null }),
            "the world transfer arrive at the client",
            LoopbackDeadline.Applied,
            WireLoopbackServer.Observing(wire));
        foreach (ServerSnapshotMessage snapshot in WorldTransferReassembler.Reassemble(wire.Frames.Select(frame => frame.Decoded.Message))
            .OfType<ServerSnapshotMessage>().Where(message => message.Snapshot.World is not null))
        {
            Assert.Equal(ServerWorldSize, snapshot.Snapshot.World!.Value.Width);
            RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, snapshot.Snapshot);
        }

        Assert.Equal(ServerWorldSize, client.Map.Width);
        Assert.Equal(ServerWorldSize, client.Map.Depth);
        output.WriteLine($"client floor {ClientFloorSize} -> server world {ServerWorldSize}");
    }
}
