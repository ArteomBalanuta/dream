using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// PersistentWorldSeedDeterminismIntegrationTests' wire check: a persistent server over loopback never
// puts the world seed on the wire. Its own class so it runs in parallel with the other seed runs.
public class PersistentWorldSeedLoopbackIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PersistentServer_OverLoopback_NeverPutsTheSeedOnTheWire()
    {
        using TempDatabase database = new();
        ServerPersistenceBundle bundle = database.OpenBundle();
        ulong seed = bundle.SimulationSeed!.Value;
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(ServerBootstrap.CreateAuthoritativeHost(bundle));
        Assert.Equal(seed, server.Host.Runtime!.SimulationSeed);

        await using WireLoopbackClient a = await server.ConnectAndJoinAsync(PrimarySession);
        await using WireLoopbackClient b = await server.ConnectAndJoinAsync(SessionB);
        WireLoopbackClient[] clients = [a, b];

        // Build, fight the runtime's own wolf until it dies and respawns, and request a full
        // world resync, so join, snapshot, action, damage and full-world frames are all captured.
        int builds = 0;
        Vector3[] buildCells = [new(7f, 1f, 5f), new(3f, 1f, 5f), new(5f, 1f, 7f)];
        for (int tick = 0; tick < 240; tick++)
        {
            foreach (WireLoopbackClient client in clients)
            {
                Vector3 facing = server.DirectionToNearestEnemy(client.SessionId) ?? Vector3.UnitZ;
                await client.SendCommandAsync(PlayerInteractionMode.Combat, PlayerMoveIntent.None, facing, attack: true);
            }

            if (tick % 20 == 2 && builds < buildCells.Length)
            {
                await a.SendActionAsync(new BuildUseIntent(buildCells[builds++]));
            }

            if (tick == 150)
            {
                await b.SendAsync(new ClientWorldResyncRequestMessage(0));
            }

            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }

        ulong lastTick = server.Host.Runtime!.CurrentServerTick;
        foreach (WireLoopbackClient client in clients)
        {
            await client.WaitForSnapshotTickAsync(lastTick);
            Assert.Null(client.ReadFault);
        }

        Assert.Contains(b.Frames, frame => frame.Decoded.Message is ServerSnapshotMessage { Snapshot.World: not null });
        Assert.Contains(a.Frames, frame => frame.Decoded.Message is ServerSnapshotMessage message && message.Snapshot.DamageEvents.Count > 0);

        byte[][] needles = SeedRepresentations(seed);
        long scannedBytes = 0;
        foreach (WireLoopbackClient client in clients)
        {
            byte[] stream = client.RawStream;
            scannedBytes += stream.Length;
            foreach (byte[] needle in needles)
            {
                Assert.True(
                    stream.AsSpan().IndexOf(needle) < 0,
                    $"{client.SessionId}'s wire stream contains the seed as '{Describe(needle)}'.");
            }

            foreach (WireFrame frame in client.Frames)
            {
                foreach (byte[] needle in needles)
                {
                    Assert.True(frame.Payload.AsSpan().IndexOf(needle) < 0, $"A {frame.Decoded.Envelope.MessageKind} frame contains the seed.");
                }
            }
        }

        output.WriteLine(
            $"scanned {scannedBytes} bytes in {clients.Sum(client => client.Frames.Count)} frames for {needles.Length} seed spellings");
    }
}
