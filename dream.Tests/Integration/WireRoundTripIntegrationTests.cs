using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Integration;

// WIRE-1 (Vector3 read), WIRE-2 (zero-copy deframing) and WIRE-3 (single-pass decode) each have
// their own tests. This drives a real snapshot through all three together: framed by the
// length-prefixed framer, delivered to FrameReceiveBuffer in arbitrary chunk splits, deframed as
// spans that alias the receive buffer, and decoded straight from those spans.
public class WireRoundTripIntegrationTests(ITestOutputHelper output)
{
    private const int FrameCount = 6;

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1234)]
    [InlineData(98765)]
    public void FramedSnapshots_RandomChunkSplits_DecodeToOriginals(int seed)
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        ServerSnapshotMessage[] originals = new ServerSnapshotMessage[FrameCount];
        byte[][] encoded = new byte[FrameCount][];
        List<byte> wire = [];
        for (int i = 0; i < FrameCount; i++)
        {
            originals[i] = new ServerSnapshotMessage(CreateSnapshot((ulong)(500 + i), actorCount: 3 + (i * 4), seed: i));
            encoded[i] = codec.Encode(Envelope(originals[i], (ulong)(10 + i)), originals[i]);
            wire.AddRange(framer.Frame(encoded[i]));
        }

        byte[] stream = wire.ToArray();
        Random random = new(seed);
        // Small initial capacity so the buffer also compacts and grows while frames are pending.
        FrameReceiveBuffer buffer = new(initialCapacity: 64);
        List<DecodedProtocolMessage> decoded = [];
        int offset = 0;
        while (offset < stream.Length)
        {
            // Mostly small splits (header and payload torn apart), sometimes several frames at once.
            int chunk = random.Next(4) == 0
                ? random.Next(1, stream.Length)
                : random.Next(1, 48);
            chunk = Math.Min(chunk, stream.Length - offset);
            buffer.Append(stream.AsSpan(offset, chunk));
            offset += chunk;
            DrainInto(buffer, framer, codec, decoded);
        }

        Assert.Equal(0, buffer.BufferedBytes);
        Assert.Equal(FrameCount, decoded.Count);
        for (int i = 0; i < FrameCount; i++)
        {
            AssertDecodedEquals(codec, originals[i], encoded[i], (ulong)(10 + i), decoded[i]);
        }
    }

    [Fact]
    public void SeveralFramesInOneAppend_AllDecode_AndOverwritingTheBufferLeavesDecodedMessagesIntact()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        ServerSnapshotMessage[] originals =
        [
            new(CreateSnapshot(900, actorCount: 5, seed: 1)),
            new(CreateSnapshot(901, actorCount: 9, seed: 2)),
            new(CreateSnapshot(902, actorCount: 2, seed: 3))
        ];
        byte[][] encoded = originals
            .Select((message, index) => codec.Encode(Envelope(message, (ulong)(70 + index)), message))
            .ToArray();
        byte[] batch = encoded.SelectMany(payload => framer.Frame(payload)).ToArray();

        FrameReceiveBuffer buffer = new(initialCapacity: batch.Length);
        buffer.Append(batch);
        List<DecodedProtocolMessage> decoded = [];
        DrainInto(buffer, framer, codec, decoded);

        Assert.Equal(originals.Length, decoded.Count);
        Assert.Equal(0, buffer.BufferedBytes);
        int capacity = buffer.Capacity;

        // The buffer is empty, so the next append writes from offset 0 over the bytes the earlier
        // payload spans pointed at. Decoded messages must not alias those bytes.
        byte[] garbage = new byte[capacity];
        Array.Fill(garbage, (byte)'7');
        buffer.Append(garbage);
        Assert.Equal(capacity, buffer.Capacity);

        for (int i = 0; i < originals.Length; i++)
        {
            AssertDecodedEquals(codec, originals[i], encoded[i], (ulong)(70 + i), decoded[i]);
        }

        // A later valid frame still decodes after the garbage is discarded by a fresh buffer.
        FrameReceiveBuffer second = new(initialCapacity: 16);
        second.Append(framer.Frame(encoded[1]));
        List<DecodedProtocolMessage> again = [];
        DrainInto(second, framer, codec, again);
        AssertDecodedEquals(codec, originals[1], encoded[1], 71, Assert.Single(again));
    }

    [Fact]
    public void EndToEndDecodePerSnapshot_AllocationIsMeasured()
    {
        TransportJsonCodec codec = new();
        LengthPrefixedMessageFramer framer = new();
        ServerSnapshotMessage message = new(CreateSnapshot(1000, actorCount: 24, seed: 5));
        byte[] encoded = codec.Encode(Envelope(message, 1), message);
        byte[] framed = framer.Frame(encoded);
        FrameReceiveBuffer buffer = new(initialCapacity: framed.Length * 2);
        int decodedCount = 0;

        // System.Text.Json rents scratch buffers from ArrayPool.Shared, which other tests running
        // in parallel can make it trim (Gen2 GC); a re-rent then shows up as a one-off allocation
        // in whichever measurement it lands in. The lowest of three measurements is the steady state.
        long endToEnd = MinOfThree(() => AllocationAssert.MeasureBytesPerIteration(() =>
        {
            buffer.Append(framed);
            while (buffer.TryReadPayload(framer, out ReadOnlySpan<byte> payload))
            {
                DecodedProtocolMessage decoded = codec.Decode(payload);
                if (decoded.Message is ServerSnapshotMessage)
                {
                    decodedCount++;
                }
            }
        }, iterations: 50, warmupIterations: 5));

        long decodeOnly = MinOfThree(() => AllocationAssert.MeasureBytesPerIteration(() => codec.Decode(encoded), iterations: 50, warmupIterations: 5));
        long deframeOnly = MinOfThree(() => AllocationAssert.MeasureBytesPerIteration(() =>
        {
            buffer.Append(framed);
            while (buffer.TryReadPayload(framer, out ReadOnlySpan<byte> _))
            {
            }
        }, iterations: 50, warmupIterations: 5));

        output.WriteLine(
            $"End-to-end deframe+decode, 24-actor snapshot ({framed.Length} B framed): {endToEnd} B/snapshot; " +
            $"decode only {decodeOnly} B; deframe only {deframeOnly} B.");
        Assert.Equal(55 * 3, decodedCount);
        Assert.Equal(0, deframeOnly);
        // Deframing adds nothing over decoding the payload directly (small slack for run-to-run noise).
        Assert.True(endToEnd <= decodeOnly + 64, $"End-to-end {endToEnd} B exceeds decode-only {decodeOnly} B by more than 64 B.");
    }

    private static long MinOfThree(Func<long> measure)
    {
        return Math.Min(measure(), Math.Min(measure(), measure()));
    }

    private static void DrainInto(
        FrameReceiveBuffer buffer,
        IMessageFramer framer,
        IProtocolCodec codec,
        List<DecodedProtocolMessage> decoded)
    {
        while (buffer.TryReadPayload(framer, out ReadOnlySpan<byte> payload))
        {
            decoded.Add(codec.Decode(payload));
        }
    }

    private static void AssertDecodedEquals(
        TransportJsonCodec codec,
        ServerSnapshotMessage original,
        byte[] originalBytes,
        ulong sequence,
        DecodedProtocolMessage decoded)
    {
        Assert.Equal(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, original.MessageKind, sequence), decoded.Envelope);
        ServerSnapshotMessage message = Assert.IsType<ServerSnapshotMessage>(decoded.Message);
        ServerSnapshotEnvelope expected = original.Snapshot;
        ServerSnapshotEnvelope actual = message.Snapshot;

        Assert.Equal(expected.ServerTick, actual.ServerTick);
        Assert.Equal(expected.InteractionMode, actual.InteractionMode);
        Assert.Equal(expected.WorldChecksum, actual.WorldChecksum);
        Assert.Equal(expected.WorldChecksumSequence, actual.WorldChecksumSequence);
        Assert.Equal(
            expected.LastAcceptedCommandSequences.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            actual.LastAcceptedCommandSequences.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(expected.Actors, actual.Actors);
        Assert.Equal(expected.DamageEvents, actual.DamageEvents);
        Assert.Equal(expected.WorldMutations.Count, actual.WorldMutations.Count);
        for (int i = 0; i < expected.WorldMutations.Count; i++)
        {
            WorldMutationDelta left = expected.WorldMutations[i];
            WorldMutationDelta right = actual.WorldMutations[i];
            Assert.Equal(left with { FracturePieces = null }, right with { FracturePieces = null });
            Assert.Equal(left.FracturePieces ?? [], right.FracturePieces ?? []);
        }

        // Byte-identical re-encode covers every field, including ones not compared above.
        Assert.Equal(originalBytes, codec.Encode(decoded.Envelope, message));
    }

    private static TransportEnvelope Envelope(ServerSnapshotMessage message, ulong sequence)
    {
        return new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence);
    }

    private static ServerSnapshotEnvelope CreateSnapshot(ulong tick, int actorCount, int seed)
    {
        Random random = new(seed);
        ActorSnapshot[] actors = new ActorSnapshot[actorCount];
        Dictionary<string, ulong> sequences = new(StringComparer.Ordinal);
        for (int i = 0; i < actorCount; i++)
        {
            bool isPlayer = i % 3 == 0;
            string? sessionId = isPlayer ? $"session-{seed}-{i}" : null;
            actors[i] = new ActorSnapshot(
                isPlayer ? 100 + i : 2000 + i,
                isPlayer ? CreatureKind.Player : CreatureKind.Enemy,
                sessionId,
                NextVector(random),
                NextVector(random).Normalized(),
                90 - i,
                100,
                IsAlive: i % 5 != 4,
                isPlayer ? PlayerInteractionMode.Combat : null,
                isPlayer ? new PlayerCombatStatsSnapshot(16, 3f, 11) : null,
                isPlayer ? $"Player {i}" : "Wolf",
                isPlayer ? new PlayerProgressionSnapshot(2, 14, 1023) : null,
                isPlayer ? null : EnemyBehaviorState.Attacking);
            if (sessionId != null)
            {
                sequences[sessionId] = (ulong)(100 + i);
            }
        }

        Vector3 moveCenter = new(3.5f + (float)random.NextDouble(), 1.5f, -0.1f);
        GeometryFracturePiece[] pieces =
        [
            new(new Vector3(3.2f, 1.1f, 3.2f), 0.2f),
            new(new Vector3(1f / 3f, -1e-7f, 123.456f), 0.18f)
        ];
        WorldMutationDelta[] mutations =
        [
            new(WorldMutationKind.CubePlaced, new Vector3(1f, 2f, 3f), MaterialId: GeometryMaterialId.Stone, Sequence: tick * 4, ActorId: 101),
            WorldMutationDelta.Moved(new Vector3(3f, 1f, 0f), moveCenter) with { Sequence = (tick * 4) + 1, ActorId = 101 },
            WorldMutationDelta.Fractured(new Vector3(3f, 1f, 3f), GeometryMaterialId.Sand, pieces) with { Sequence = (tick * 4) + 2 },
            new(WorldMutationKind.CubeDestroyed, new Vector3(-0f, 0.1f, 1e6f), Sequence: (tick * 4) + 3)
        ];

        return new ServerSnapshotEnvelope(
            tick,
            sequences,
            actors,
            PlayerInteractionMode.Combat,
            null,
            mutations,
            [new DamageEventDelta(2000, CreatureKind.Enemy, 5, false, 35, 40)],
            WorldChecksum: 0xFEEDFACECAFEBEEFUL ^ tick,
            WorldChecksumSequence: (tick * 4) + 3);
    }

    private static Vector3 NextVector(Random random)
    {
        return new Vector3(
            (float)((random.NextDouble() * 200d) - 100d),
            (float)(random.NextDouble() * 10d),
            (float)((random.NextDouble() * 200d) - 100d));
    }
}
