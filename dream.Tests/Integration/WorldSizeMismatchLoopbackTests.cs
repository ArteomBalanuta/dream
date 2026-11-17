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
        await wire.SendAsync(new ClientWorldResyncRequestMessage(0));
        for (int i = 0; i < 4; i++)
        {
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }

        await wire.WaitForAsync(
            frames => WorldTransferReassembler.Reassemble(frames.Select(frame => frame.Decoded.Message))
                .Any(message => message is ServerSnapshotMessage { Snapshot.World: not null }),
            "world transfer");
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
