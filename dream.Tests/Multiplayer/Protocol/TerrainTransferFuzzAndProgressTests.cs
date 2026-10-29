using System.Text;
using System.Text.Json;
using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using static Dream.Tests.Multiplayer.Protocol.TerrainTransferTestData;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Multiplayer.Protocol;

// Protocol v8: damaged terrain frames always end the transfer with a protocol error, and terrain
// frames count as progress for the transfer's deadlines and the HUD.
public sealed class TerrainTransferFuzzAndProgressTests
{
    private const int Id = 3;
    private static readonly TransportJsonCodec Codec = new();
    private static readonly byte[] PayloadMarker = Encoding.UTF8.GetBytes("\"payload\":");

    // ---- Fuzz -----------------------------------------------------------------------------------

    [Fact]
    public void RandomFlipsAndTruncations_OfRealFragments_AlwaysEndInAProtocolError_WithBoundedMemory()
    {
        TerrainTransferPayload payload = Payload();
        JoinAcceptedMessage carrier = Carrier(payload, Id);
        List<TerrainChunkMessage> fragments = Fragments(payload, Id);
        byte[][] frames = fragments.Select(fragment => Encode(fragment)).ToArray();
        Random random = new(20261029);
        const int Runs = 500;

        // A clean pass first: the fixture itself must complete.
        Replay(carrier, frames, mutatedIndex: -1, mutated: [], out Exception? clean);
        Assert.Null(clean);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int truncations = 0;
        for (int run = 0; run < Runs; run++)
        {
            int victim = random.Next(frames.Length);
            byte[] mutated = (byte[])frames[victim].Clone();
            int payloadStart = IndexOf(mutated, PayloadMarker) + PayloadMarker.Length;
            if (random.Next(4) == 0)
            {
                // Truncation anywhere inside the payload (never the whole frame).
                mutated = mutated[..random.Next(payloadStart, mutated.Length - 1)];
                truncations++;
            }
            else
            {
                // Flip bits of one byte inside the payload, not the envelope's closing brace.
                int position = random.Next(payloadStart, mutated.Length - 1);
                mutated[position] ^= (byte)(1 << random.Next(8));
            }

            Replay(carrier, frames, victim, mutated, out Exception? error);

            Assert.True(error != null, $"run {run}: a damaged fragment {victim} completed the transfer");
            Assert.True(
                error is JsonException or InvalidOperationException or InvalidDataException,
                $"run {run}: fragment {victim} ended in {error!.GetType().Name}: {error.Message}");
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(truncations > 50, "the fuzz should truncate as well as flip");
        // Each run decodes a few tens of KB; nothing a damaged frame says may make it allocate more.
        Assert.True(allocated < Runs * 4L * 1024 * 1024, $"fuzz allocated {allocated} bytes");
    }

    private static byte[] Encode<T>(T message) where T : IServerToClientMessage =>
        Codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message);

    // Feeds the carrier, the terrain frames (the mutated one in place of frames[mutatedIndex]) and a
    // final world chunk. error is the first protocol failure, null when the transfer completed.
    private static void Replay(JoinAcceptedMessage carrier, byte[][] frames, int mutatedIndex, byte[] mutated, out Exception? error)
    {
        error = null;
        try
        {
            List<IServerToClientMessage> delivered = [];
            WorldTransferReassembler reassembler = new();
            reassembler.Accept(carrier, delivered.Add);
            for (int i = 0; i < frames.Length; i++)
            {
                byte[] frame = i == mutatedIndex ? mutated : frames[i];
                IServerToClientMessage message = (IServerToClientMessage)Codec.Decode(frame).Message;
                reassembler.Accept(message, delivered.Add);
            }

            reassembler.Accept(FinalWorldChunk(Id), delivered.Add);
            Assert.NotNull(Assert.IsType<JoinAcceptedMessage>(delivered[0]).ReceivedTerrain);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or InvalidDataException or FormatException or ArgumentException)
        {
            error = ex;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

    // ---- Deadline and progress ------------------------------------------------------------------

    [Fact]
    public void TerrainFrames_CountAsProgress_ForTheStallTimeout()
    {
        ManualTimeProvider clock = new();
        TerrainTransferPayload payload = Payload();
        List<TerrainChunkMessage> fragments = Fragments(payload, Id);
        (WorldTransferReassembler reassembler, _) = Started(Carrier(payload, Id), clock: clock);

        // Each gap is under the 30 s progress timeout, the total is far over it.
        for (int i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(25));
            reassembler.CheckDeadline();
            reassembler.Accept(fragments[i], _ => { });
            reassembler.CheckDeadline();
        }

        Assert.Equal(3, reassembler.Current!.TerrainFragmentsReceived);

        clock.Advance(TimeSpan.FromSeconds(31));
        WorldTransferLimitException stalled = Assert.Throws<WorldTransferLimitException>(() => reassembler.CheckDeadline());
        Assert.Contains("stalled", stalled.Message);
        Assert.Contains("3 terrain fragments", stalled.Message);
    }

    [Fact]
    public void TerrainFrames_DoNotExtendTheOverallDeadline()
    {
        ManualTimeProvider clock = new();
        TerrainTransferPayload payload = Payload();
        List<TerrainChunkMessage> fragments = Fragments(payload, Id);
        WorldTransferLimits limits = new() { Deadline = TimeSpan.FromSeconds(60), ProgressTimeout = TimeSpan.FromSeconds(30) };
        (WorldTransferReassembler reassembler, _) = Started(Carrier(payload, Id), limits, clock);

        clock.Advance(TimeSpan.FromSeconds(25));
        reassembler.Accept(fragments[0], _ => { });
        clock.Advance(TimeSpan.FromSeconds(25));
        reassembler.Accept(fragments[1], _ => { });
        reassembler.CheckDeadline();
        clock.Advance(TimeSpan.FromSeconds(25));
        reassembler.Accept(fragments[2], _ => { });

        WorldTransferLimitException late = Assert.Throws<WorldTransferLimitException>(() => reassembler.CheckDeadline());
        Assert.Contains("did not finish", late.Message);
    }

    [Fact]
    public void ProgressTimeout_CatchesATransferThatStopsAfterTheCarrier()
    {
        ManualTimeProvider clock = new();
        (WorldTransferReassembler reassembler, _) = Started(Carrier(Payload(), Id), clock: clock);

        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.Throws<WorldTransferLimitException>(() => reassembler.CheckDeadline());
    }

    [Fact]
    public async Task Session_CountsTerrainFrameBytes_TowardTheWorldTransferProgress()
    {
        ManualTimeProvider clock = new();
        GatedTransport transport = new("terrain-progress");
        await using RemoteClientSession session = new(
            transport,
            Codec,
            new LengthPrefixedMessageFramer(1 << 20),
            new RemoteClientOptions("127.0.0.1", 1, "session-a", "A", "terrain-progress"),
            clock,
            WorldTransferLimits.Default);
        session.StartBackgroundReceive();
        TerrainTransferPayload payload = Payload();
        List<TerrainChunkMessage> fragments = Fragments(payload, Id);
        LengthPrefixedMessageFramer framer = new(1 << 20);

        byte[] carrier = Codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "server.join-accepted", 1), Carrier(payload, Id));
        transport.Feed(framer.Frame(carrier));
        await WaitUntilAsync(() => session.InboundFrameCount == 1);
        Assert.True(session.TryGetWorldTransferProgress(out long bytes, out int chunks, out _));
        Assert.Equal(carrier.Length, bytes);

        long expected = carrier.Length;
        for (int i = 0; i < fragments.Count; i++)
        {
            byte[] frame = Codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, TerrainChunkMessage.Kind, (ulong)(i + 2)), fragments[i]);
            expected += frame.Length;
            transport.Feed(framer.Frame(frame));
            long frames = i + 2;
            await WaitUntilAsync(() => session.InboundFrameCount == frames);
            Assert.True(session.TryGetWorldTransferProgress(out bytes, out chunks, out _));
            Assert.Equal(expected, bytes);
            Assert.Equal(0, chunks); // world chunks only
        }

        Assert.True(session.IsReceivingWorld);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(5);
        }
    }
}
