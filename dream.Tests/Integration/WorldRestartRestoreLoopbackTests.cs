using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Worlds;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Integration;

// Persistence wave B: world 2 (not the default, and a different size) is served over loopback, a
// lamp is placed in it and saved, and a restart of world 2 restores the lamp, the descriptor and the
// stored seed. Opening world 1 of the same database shows none of it.
public sealed class WorldRestartRestoreLoopbackTests(ITestOutputHelper output)
{
    private const string Session = "world-two";
    private const int SecondWorldSize = 30;
    private const ulong SecondSeed = 7777;

    [Fact]
    public async Task World2_RestoresItsLamp_AndItsDescriptor_AfterARestart_WhileWorld1StaysEmpty()
    {
        using MigrationTestDatabase database = new();
        WorldRecord second = WorldBundles.OpenCatalog(database)
            .Create(new WorldCreateOptions("second", SecondWorldSize, SecondSeed));
        Assert.Equal(new WorldId(2), second.Descriptor.Id);
        Assert.Equal(SecondWorldSize, second.Descriptor.Width);

        Vector3 lampCell;
        await using (WireLoopbackServer server = await WireLoopbackServer.StartAsync(
            ServerBootstrap.CreateAuthoritativeHost(WorldBundles.Bind(database.Schema, second))))
        {
            Assert.Equal(SecondWorldSize, server.Runtime.Map.Width);
            Assert.Equal(SecondSeed, server.Runtime.SimulationSeed);
            await using WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
            AssertDescriptor(client, second.Descriptor);
            lampCell = await CrossWaveT2PersistenceLoopbackTests.PlaceLampAsync(server, client);
            await server.DisconnectAsync(client);
            await TickAsync(server, 2);
            Assert.True(server.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(1L, WorldBundles.Count(database, "placed_cube_instances", new WorldId(2)));
        Assert.Equal(0L, WorldBundles.Count(database, "placed_cube_instances", WorldId.Default));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM placed_cube_instances WHERE material_id = 'Lamp';"));

        // Restart world 2: same size, same seed, the lamp is back, and the join carries the descriptor.
        await using (WireLoopbackServer server = await WireLoopbackServer.StartAsync(
            ServerBootstrap.CreateAuthoritativeHost(WorldBundles.Open(database, new WorldId(2)))))
        {
            Assert.Equal(SecondWorldSize, server.Runtime.Map.Width);
            Assert.Equal(SecondWorldSize, server.Runtime.Map.Depth);
            Assert.Equal(SecondSeed, server.Runtime.SimulationSeed);
            Assert.Equal(
                GeometryMaterialId.Lamp,
                server.Runtime.Map.TryGetCubeAtCell((int)lampCell.X, (int)lampCell.Y, (int)lampCell.Z)?.MaterialId);
            await using WireLoopbackClient client = await server.ConnectAndJoinAsync(Session);
            AssertDescriptor(client, second.Descriptor);
            await server.DisconnectAsync(client);
            await TickAsync(server, 2);
        }

        SqliteConnection.ClearAllPools();

        // World 1 of the same database: its own 56-cell map, no lamp.
        using (ServerPersistenceBundle bundle = WorldBundles.Open(database, WorldId.Default))
        using (DedicatedServerHost world1 = ServerBootstrap.CreateAuthoritativeHost(bundle))
        {
            world1.Runtime!.WriteCheckpointOnDispose = false;
            Assert.Equal(WorldDefaults.StartupFloorSize, world1.Runtime.Map.Width);
            Assert.Null(world1.Runtime.Map.TryGetCubeAtCell((int)lampCell.X, (int)lampCell.Y, (int)lampCell.Z));
            Assert.NotEqual(SecondSeed, world1.Runtime.SimulationSeed);
        }

        output.WriteLine($"world 2 ({SecondWorldSize}x{SecondWorldSize}) restored its lamp at {lampCell}");
    }

    private static void AssertDescriptor(WireLoopbackClient client, WorldDescriptor expected)
    {
        JoinAcceptedMessage join = client.Frames
            .Select(frame => frame.Decoded.Message)
            .OfType<JoinAcceptedMessage>()
            .First();
        Assert.NotNull(join.World);
        WorldDescriptorWire wire = join.World!;
        Assert.Equal(expected, wire.ToDescriptor());
    }

    private static async Task TickAsync(WireLoopbackServer server, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }
    }
}
