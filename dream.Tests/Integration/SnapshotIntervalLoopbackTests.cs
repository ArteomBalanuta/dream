using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.World;
using Dream.Tests.Server;
using static Dream.Tests.Integration.ProtocolV2SharedSnapshotLoopbackIntegrationTests;

namespace Dream.Tests.Integration;

// NETR-7 over real loopback TCP with the runtime-backed host, at the default snapshot interval
// of 2 ticks (32 Hz): the server still simulates every tick, but a tick whose snapshot is held
// back must lose nothing. Its world mutations ride in the next snapshot, its action results
// follow that snapshot, and the client's world still matches every checksum the server sends.
public class SnapshotIntervalLoopbackTests(ITestOutputHelper output)
{
    private static readonly ServerConnectionOptions IntervalTwo =
        ServerConnectionTestFixtures.WithoutLiveness with { SnapshotIntervalTicks = 2 };

    [Fact]
    public async Task MutationOnASkippedTick_ArrivesInTheNextSnapshot_AndItsResultFollowsThatSnapshot()
    {
        await using ServerUnderTest server = await ServerUnderTest.StartAsync(IntervalTwo);
        await using RawClient primary = await server.ConnectAndJoinAsync(PrimarySession);
        DedicatedServerService service = server.Service;

        // Let the player settle on the floor (a build right after spawn is refused), and stop on a
        // tick whose snapshot went out; the tick after it is then held back.
        bool lastAdvanceBroadcast = false;
        for (int i = 0; i < 40 && (i < 16 || !lastAdvanceBroadcast); i++)
        {
            long broadcast = service.SnapshotsBroadcast;
            await SendCommandAndWaitAsync(server, primary);
            await server.TickAsync();
            lastAdvanceBroadcast = service.SnapshotsBroadcast > broadcast;
        }

        Assert.True(lastAdvanceBroadcast, "No snapshot was broadcast.");
        Assert.True(await server.FlushAllAsync());

        // The build runs on the next tick, whose snapshot is skipped.
        long skipped = service.SkippedSnapshotAdvances;
        EnqueuePrimaryBuild(server, FirstFreeBuildCell(server));
        await SendCommandAndWaitAsync(server, primary);
        await server.TickAsync();
        ulong skippedTick = server.Host.Runtime!.CurrentServerTick;
        Assert.Equal(skipped + 1, service.SkippedSnapshotAdvances);
        Assert.True(await server.FlushAllAsync());
        Assert.DoesNotContain(primary.Frames, frame => frame.Decoded.Message is ServerActionMessage);

        // The next tick's snapshot carries the skipped tick's placement; the result comes after it.
        await SendCommandAndWaitAsync(server, primary);
        await server.TickAsync();
        ulong broadcastTick = server.Host.Runtime!.CurrentServerTick;
        Assert.True(await server.FlushAllAsync());
        await WaitUntilAsync(
            () => primary.Frames.Any(frame => frame.Decoded.Message is ServerActionMessage),
            "The build's action result never arrived.");

        IReadOnlyList<Frame> frames = primary.Frames;
        Assert.DoesNotContain(
            frames,
            frame => frame.Decoded.Message is ServerSnapshotMessage message && message.Snapshot.ServerTick == skippedTick);
        int snapshotIndex = IndexOf(frames, frame =>
            frame.Decoded.Message is ServerSnapshotMessage message && message.Snapshot.ServerTick == broadcastTick);
        Assert.True(snapshotIndex >= 0, $"No snapshot for tick {broadcastTick}.");
        ServerSnapshotEnvelope snapshot = ((ServerSnapshotMessage)frames[snapshotIndex].Decoded.Message).Snapshot;
        int resultIndex = IndexOf(frames, frame => frame.Decoded.Message is ServerActionMessage);
        ServerActionMessage result = (ServerActionMessage)frames[resultIndex].Decoded.Message;
        output.WriteLine(
            $"held-back tick {skippedTick}, snapshot tick {broadcastTick}: " +
            $"{string.Join(", ", snapshot.WorldMutations.Select(mutation => $"{mutation.Kind}@{mutation.CellPosition}"))}; result {result.Result}");
        Assert.True(result.Result.Accepted, "The build was rejected.");
        Assert.Contains(snapshot.WorldMutations, mutation => mutation.Kind == WorldMutationKind.CubePlaced);
        Assert.Equal(skippedTick, result.Result.ServerTick);
        Assert.True(resultIndex > snapshotIndex, "The action result went out before the snapshot that carries its mutation.");

        // Acks still advance: the snapshot carries every command accepted up to its tick,
        // including the held-back tick's.
        ulong[] acks = frames
            .Select(frame => frame.Decoded.Message)
            .OfType<ServerSnapshotMessage>()
            .Select(message => message.Snapshot.LastAcceptedCommandSequences.TryGetValue(PrimarySession, out ulong ack) ? ack : 0UL)
            .ToArray();
        Assert.True(acks.Length >= 2, $"Only {acks.Length} snapshots arrived.");
        Assert.True(acks[^1] > acks[^2], $"The ack did not advance across the held-back tick ({acks[^2]} -> {acks[^1]}).");
    }

    [Fact]
    public async Task ThreeHundredTickScriptedSession_AtInterval2_ClientWorldMatchesEveryChecksum()
    {
        await using ServerUnderTest server = await ServerUnderTest.StartAsync(IntervalTwo);
        await using RawClient primary = await server.ConnectAndJoinAsync(PrimarySession);
        await using RawClient walker = await server.ConnectAndJoinAsync(EarlySecondary);
        RawClient[] clients = [primary, walker];

        // Builds spread over the run, on odd and even ticks, so some land on held-back ticks.
        const int Ticks = 300;
        int builds = 0;
        bool lastAdvanceBroadcast = false;
        for (int i = 0; i < Ticks; i++)
        {
            await primary.SendCommandAsync(walking: false);
            await walker.SendCommandAsync(walking: i % 70 < 25);
            if (builds < BuildCells.Length && i == 20 + (builds * 33))
            {
                EnqueuePrimaryBuild(server, builds++);
            }

            long broadcastBefore = server.Service.SnapshotsBroadcast;
            await server.TickAsync();
            lastAdvanceBroadcast = server.Service.SnapshotsBroadcast > broadcastBefore;
            Assert.True(await server.FlushAllAsync(), "A connection did not drain its queue.");
        }

        // One more tick if the last one was held back, so its deltas go out too.
        if (!lastAdvanceBroadcast)
        {
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }

        ulong lastTick = server.Host.Runtime!.CurrentServerTick;
        foreach (RawClient client in clients)
        {
            await client.WaitForSnapshotTickAsync(lastTick);
        }

        Assert.Equal(BuildCells.Length, builds);
        Assert.InRange(server.Service.SnapshotsBroadcast, (Ticks / 2) - 2, (Ticks / 2) + 3);
        ulong serverChecksum = WorldStateChecksum.Compute(server.Host.Runtime!.Map);
        int floorSize = server.Host.Runtime!.Map.Width;
        foreach (RawClient client in clients)
        {
            Assert.Null(client.ReadFault);
            AssertEnvelopeSequencesAreGapFree(client);
            ulong[] snapshotTicks = client.Snapshots()
                .Select(frame => ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot.ServerTick)
                .ToArray();
            for (int i = 1; i < snapshotTicks.Length; i++)
            {
                Assert.Equal(2UL, snapshotTicks[i] - snapshotTicks[i - 1]);
            }

            ClientWorldReplay replay = client.Replay(floorSize);
            output.WriteLine(
                $"{client.SessionId}: snapshots {snapshotTicks.Length}, checksums verified {replay.ChecksumsVerified}, " +
                $"mismatches {replay.ChecksumMismatches}, mutations {replay.MutationsApplied}");
            // A checksum goes out every 64 ticks, carried by the first snapshot at or after it.
            Assert.True(replay.ChecksumsVerified >= 4, $"{client.SessionId} verified {replay.ChecksumsVerified} checksums.");
            Assert.Equal(0, replay.ChecksumMismatches);
            Assert.Equal(serverChecksum, replay.FinalChecksum);
            Assert.True(replay.MutationsApplied >= primary.AcceptedActions, $"{client.SessionId} applied {replay.MutationsApplied} mutations.");
        }

        Assert.True(primary.AcceptedActions >= BuildCells.Length / 2, $"Only {primary.AcceptedActions} builds were accepted.");
    }

    // The startup terrain may already fill some of the reachable cells.
    private static int FirstFreeBuildCell(ServerUnderTest server)
    {
        Map map = server.Host.Runtime!.Map;
        for (int i = 0; i < BuildCells.Length; i++)
        {
            int x = (int)BuildCells[i].X;
            int y = (int)BuildCells[i].Y;
            int z = (int)BuildCells[i].Z;
            if (map.TryGetCubeAtCell(x, y, z) == null && map.TryGetGeometryCellAtCell(x, y, z) == null)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Every build cell is occupied.");
    }

    // Sends one command and waits until the server has read it, so the next tick consumes it.
    private static async Task SendCommandAndWaitAsync(ServerUnderTest server, RawClient client)
    {
        ServerConnection connection = server.ConnectionFor(client);
        long before = connection.FramesReceived;
        await client.SendCommandAsync(walking: false);
        await WaitUntilAsync(() => connection.FramesReceived > before, "The server never read the command.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        DateTime deadline = DateTime.UtcNow + ProtocolV2SharedSnapshotLoopbackIntegrationTests.Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(1);
        }
    }

    private static int IndexOf(IReadOnlyList<Frame> frames, Func<Frame, bool> predicate)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            if (predicate(frames[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
