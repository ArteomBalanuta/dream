using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Multiplayer.Protocol;
using RawClient = Dream.Tests.Integration.ProtocolV2SharedSnapshotLoopbackIntegrationTests.RawClient;
using Frame = Dream.Tests.Integration.ProtocolV2SharedSnapshotLoopbackIntegrationTests.Frame;
using ClientWorldReplay = Dream.Tests.Integration.ProtocolV2SharedSnapshotLoopbackIntegrationTests.ClientWorldReplay;
using static Dream.Tests.Integration.HandWrittenConverterConcurrencyIntegrationTests;

namespace Dream.Tests.Integration;

// HandWrittenConverterConcurrencyIntegrationTests' TCP case: three loopback clients over two hundred
// ticks decode every snapshot, match the reference encoding, and converge on the server's world.
// Its own class so it runs in parallel with the sixteen-thread encode run.
public class HandWrittenConverterLoopbackIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ThreeTcpClients_TwoHundredTicks_EverySnapshotDecodes_MatchesTheReference_AndWorldsConverge()
    {
        await using LoopbackServer server = LoopbackServer.Start();
        await using RawClient primary = await server.ConnectAndJoinAsync("session-m");
        await using RawClient early = await server.ConnectAndJoinAsync("session-z");
        await using RawClient late = await server.ConnectAndJoinAsync("session-b");
        RawClient[] clients = [primary, early, late];
        Vector3[] buildCells =
        [
            new(7f, 1f, 5f), new(3f, 1f, 5f), new(5f, 1f, 7f), new(5f, 1f, 3f),
            new(7f, 1f, 6f), new(3f, 1f, 4f), new(6f, 1f, 7f), new(4f, 1f, 3f)
        ];

        const int Ticks = 200;
        int builds = 0;
        for (int i = 0; i < Ticks; i++)
        {
            await primary.SendCommandAsync(walking: false);
            await early.SendCommandAsync(walking: i % 40 < 15);
            await late.SendCommandAsync(walking: i % 30 < 8);
            if (i % 20 == 3 && builds < buildCells.Length)
            {
                // Build use is server-originated only (NETR-6 rejects it over player.action); the
                // placed cube still reaches every client through the snapshots.
                Assert.True(server.Host.TryEnqueueServerAction(new PlayerActionMessage(
                    new PlayerSessionId("session-m"),
                    (ulong)(builds + 1),
                    new BuildUseIntent(buildCells[builds]))));
                builds++;
            }

            await server.TickAsync();
            Assert.True(await server.FlushAllAsync(), "A connection did not drain its queue.");
        }

        ulong lastTick = server.Host.Runtime!.CurrentServerTick;
        foreach (RawClient client in clients)
        {
            await client.WaitForSnapshotTickAsync(lastTick);
        }

        Assert.True(primary.AcceptedActions > 0, "No build was accepted.");
        ulong serverChecksum = WorldStateChecksum.Compute(server.Host.Runtime!.Map);
        List<Frame> allFrames = [];
        // Each replay builds its own client world from its own client's frames (no frame arrives
        // any more), so the three run at once while the checks below run.
        int floorSize = server.Host.Runtime!.Map.Width;
        Task<ClientWorldReplay>[] replays = clients.Select(client => Task.Run(() => client.Replay(floorSize))).ToArray();
        for (int c = 0; c < clients.Length; c++)
        {
            RawClient client = clients[c];
            // The client's read loop decoded every frame while the other two decoded theirs.
            Assert.Null(client.ReadFault);
            List<Frame> snapshots = client.Snapshots().ToList();
            Assert.True(snapshots.Count >= Ticks - 5, $"{client.SessionId} received {snapshots.Count} snapshots.");
            Assert.All(snapshots, frame => Assert.True(((ServerSnapshotMessage)frame.Decoded.Message).Snapshot.Actors.Count >= 3));
            foreach (Frame frame in client.Frames)
            {
                AssertMatchesReference(frame);
            }

            ClientWorldReplay replay = await replays[c];
            Assert.True(replay.ChecksumsVerified >= 2, $"{client.SessionId} verified {replay.ChecksumsVerified} checksums.");
            Assert.Equal(0, replay.ChecksumMismatches);
            Assert.Equal(serverChecksum, replay.FinalChecksum);
            allFrames.AddRange(client.Frames);
        }

        // Decode every received frame again on 16 threads at once.
        TransportJsonCodec codec = new();
        Frame[] frames = allFrames.ToArray();
        Task[] workers = Enumerable.Range(0, Threads).Select(worker => Task.Run(() =>
        {
            for (int i = worker; i < frames.Length * 2; i += Threads)
            {
                Frame frame = frames[i % frames.Length];
                DecodedProtocolMessage again = codec.Decode(frame.Payload);
                Assert.Equal(frame.Decoded.Envelope, again.Envelope);
                byte[] reencoded = JsonSerializer.SerializeToUtf8Bytes(again.Message, again.Message.GetType(), ProtocolWireOptions.V3);
                Assert.True(reencoded.AsSpan().SequenceEqual(frame.PayloadJson), $"frame {frame.Decoded.Envelope.MessageKind}#{frame.Decoded.Envelope.Sequence} re-decoded differently.");
            }
        })).ToArray();
        Assert.True(Task.WaitAll(workers, Bound), "Concurrent re-decode did not finish.");

        output.WriteLine($"ticks={lastTick} builds={primary.AcceptedActions} frames={frames.Length}");
    }
}
