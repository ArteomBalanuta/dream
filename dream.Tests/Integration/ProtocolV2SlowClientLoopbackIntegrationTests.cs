using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Multiplayer.Protocol;
using static Dream.Tests.Integration.ProtocolV2SharedSnapshotLoopbackIntegrationTests;
using Dream.Tests.Utilities;

namespace Dream.Tests.Integration;

// ProtocolV2SharedSnapshotLoopbackIntegrationTests' slow-client case: merged snapshots are encoded
// for the slow connection, decode, and keep every mutation in order. Its own class so it runs in
// parallel with the three-client run; the raw client and server harness are the main class's.
public class ProtocolV2SlowClientLoopbackIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SlowClient_GetsMergedSnapshotsEncodedForItsConnection_ThatDecode_WithMutationsInOrder()
    {
        await using ServerUnderTest server = await ServerUnderTest.StartAsync();
        await using RawClient primary = await server.ConnectAndJoinAsync(PrimarySession);
        await using RawClient fast = await server.ConnectAndJoinAsync(EarlySecondary);
        // A tiny receive window and a paused reader: the server's writer for this connection
        // blocks in the socket send, and its queued snapshots merge behind the blocked one.
        await using RawClient slow = await server.ConnectAndJoinAsync(LateSecondary, receiveBufferSize: 1024);
        ServerConnection slowConnection = server.ConnectionFor(slow);
        ulong slowJoinTick = server.Host.Runtime!.CurrentServerTick;
        slow.PauseReading();

        int builds = 0;
        int tick = 0;
        int ticksAfterFirstMerge = 0;
        while (ticksAfterFirstMerge < 120)
        {
            Assert.True(tick < 6000, $"The slow connection never coalesced (pending {slowConnection.PendingOutboundCount}).");
            await primary.SendCommandAsync(walking: false);
            await fast.SendCommandAsync(walking: tick % 90 < 30);
            // Builds keep coming while the slow client lags, so merged snapshots carry mutations.
            if (tick % 15 == 3 && builds < BuildCells.Length && (builds < 2 || slowConnection.CoalescedSnapshots > 0))
            {
                EnqueuePrimaryBuild(server, builds++);
            }

            await server.TickAsync();
            Assert.True(await OutboundDrain.FlushAsync(server.ConnectionFor(primary), ProtocolV2SharedSnapshotLoopbackIntegrationTests.Timeout));
            Assert.True(await OutboundDrain.FlushAsync(server.ConnectionFor(fast), ProtocolV2SharedSnapshotLoopbackIntegrationTests.Timeout));
            Assert.False(slowConnection.IsFaulted, "The slow connection faulted instead of coalescing.");
            ticksAfterFirstMerge += slowConnection.CoalescedSnapshots > 0 ? 1 : 0;
            tick++;
        }

        Assert.Equal(BuildCells.Length, builds);
        slow.ResumeReading();
        Assert.True(await slowConnection.FlushOutboundAsync(ProtocolV2SharedSnapshotLoopbackIntegrationTests.Timeout), "The slow connection never drained.");
        ulong lastTick = server.Host.Runtime!.CurrentServerTick;
        await slow.WaitForSnapshotTickAsync(lastTick);
        await fast.WaitForSnapshotTickAsync(lastTick);
        await primary.WaitForSnapshotTickAsync(lastTick);

        long coalesced = slowConnection.CoalescedSnapshots;
        Assert.True(coalesced > 0);
        Assert.Null(slow.ReadFault);
        AssertEnvelopeSequencesAreGapFree(slow);
        AssertSlimV2Payloads(slow);
        AssertCanonicalActorOrder(slow);

        List<Frame> slowSnapshots = slow.Snapshots().ToList();
        List<Frame> fastSnapshots = fast.Snapshots().ToList();
        // Every merged-away tick is missing from the slow stream, and nothing else is.
        int fastSinceSlowJoined = fastSnapshots.Count(frame => TickOf(frame) > slowJoinTick);
        Assert.Equal(0, server.ConnectionFor(fast).CoalescedSnapshots);
        Assert.Equal(fastSinceSlowJoined - coalesced, slowSnapshots.Count);
        Assert.All(slowSnapshots, frame => Assert.True(TickOf(frame) > slowJoinTick));

        // Merged snapshots were encoded for this connection alone: their payload matches no
        // shared payload, yet re-encoding the decoded message reproduces it exactly.
        Dictionary<ulong, Frame> fastByTick = SnapshotsByTick(fast);
        int merged = 0;
        int mergedMutations = 0;
        TransportJsonCodec codec = new();
        foreach (Frame frame in slowSnapshots)
        {
            ServerSnapshotEnvelope snapshot = ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot;
            Frame shared = fastByTick[snapshot.ServerTick];
            if (!frame.PayloadJson.AsSpan().SequenceEqual(shared.PayloadJson))
            {
                merged++;
                mergedMutations += snapshot.WorldMutations.Count;
                byte[] reencoded = codec.Encode(frame.Decoded.Envelope, (ServerSnapshotMessage)frame.Decoded.Message);
                Assert.True(
                    reencoded.AsSpan().SequenceEqual(frame.Payload),
                    $"tick {snapshot.ServerTick}: the merged snapshot does not round-trip byte for byte.");
            }
        }

        Assert.True(merged > 0, "No merged snapshot reached the wire.");
        Assert.True(mergedMutations > 0, "No merged snapshot carried a world mutation.");

        // Mutations and damage arrive in the same order, none lost or repeated, merged or not.
        string[] fastMutations = MutationStream(fast, after: slowJoinTick);
        string[] slowMutations = MutationStream(slow, after: slowJoinTick);
        Assert.Equal(fastMutations, slowMutations);
        ulong[] sequences = slow.Snapshots()
            .SelectMany(frame => ((ServerSnapshotMessage)frame.Decoded.Message).Snapshot.WorldMutations)
            .Select(mutation => mutation.Sequence)
            .ToArray();
        Assert.True(sequences.Length > 0, "No mutation reached the slow client.");
        for (int i = 1; i < sequences.Length; i++)
        {
            Assert.True(sequences[i] > sequences[i - 1], $"mutation {sequences[i]} arrived after {sequences[i - 1]}");
        }

        Assert.Equal(DamageStream(fast, slowJoinTick), DamageStream(slow, slowJoinTick));

        // The lagging client still converges on the server's world.
        ClientWorldReplay replay = slow.Replay(server.Host.Runtime!.Map.Width);
        Assert.Equal(0, replay.ChecksumMismatches);
        Assert.Equal(WorldStateChecksum.Compute(server.Host.Runtime!.Map), replay.FinalChecksum);

        output.WriteLine(
            $"ticks={lastTick} coalesced={coalesced} slowSnapshots={slowSnapshots.Count} fastSnapshots={fastSnapshots.Count} " +
            $"mergedOnWire={merged} mergedMutations={mergedMutations} mutations={sequences.Length} builds={primary.AcceptedActions}");
    }
}
